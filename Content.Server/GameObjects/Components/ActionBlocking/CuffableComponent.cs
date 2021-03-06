﻿using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.GameObjects.Components.GUI;
using Content.Server.GameObjects.Components.Items.Storage;
using Content.Server.GameObjects.Components.Mobs;
using Content.Server.GameObjects.EntitySystems.DoAfter;
using Content.Server.Interfaces.GameObjects.Components.Items;
using Content.Shared.Alert;
using Content.Shared.GameObjects.Components.ActionBlocking;
using Content.Shared.GameObjects.Components.Mobs;
using Content.Shared.GameObjects.EntitySystems;
using Content.Shared.GameObjects.EntitySystems.ActionBlocker;
using Content.Shared.GameObjects.Verbs;
using Content.Shared.Interfaces;
using Content.Shared.Utility;
using Robust.Server.GameObjects;
using Robust.Server.GameObjects.Components.Container;
using Robust.Server.GameObjects.EntitySystems;
using Robust.Shared.GameObjects;
using Robust.Shared.GameObjects.Systems;
using Robust.Shared.Interfaces.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.ViewVariables;

namespace Content.Server.GameObjects.Components.ActionBlocking
{
    [RegisterComponent]
    public class CuffableComponent : SharedCuffableComponent
    {
        /// <summary>
        /// How many of this entity's hands are currently cuffed.
        /// </summary>
        [ViewVariables]
        public int CuffedHandCount => _container.ContainedEntities.Count * 2;

        protected IEntity LastAddedCuffs => _container.ContainedEntities[_container.ContainedEntities.Count - 1];

        public IReadOnlyList<IEntity> StoredEntities => _container.ContainedEntities;

        /// <summary>
        ///     Container of various handcuffs currently applied to the entity.
        /// </summary>
        [ViewVariables(VVAccess.ReadOnly)]
        private Container _container = default!;

        private float _interactRange;
        private IHandsComponent _hands;

        public event Action OnCuffedStateChanged;

        public override void Initialize()
        {
            base.Initialize();

            _container = ContainerManagerComponent.Ensure<Container>(Name, Owner);
            _interactRange = SharedInteractionSystem.InteractionRange / 2;

            Owner.EntityManager.EventBus.SubscribeEvent<HandCountChangedEvent>(EventSource.Local, this, HandleHandCountChange);

            if (!Owner.TryGetComponent(out _hands))
            {
                Logger.Warning("Player does not have an IHandsComponent!");
            }
        }

        public override ComponentState GetComponentState()
        {
            // there are 2 approaches i can think of to handle the handcuff overlay on players
            // 1 - make the current RSI the handcuff type that's currently active. all handcuffs on the player will appear the same.
            // 2 - allow for several different player overlays for each different cuff type.
            // approach #2 would be more difficult/time consuming to do and the payoff doesn't make it worth it.
            // right now we're doing approach #1.

            if (CuffedHandCount > 0)
            {
                if (LastAddedCuffs.TryGetComponent<HandcuffComponent>(out var cuffs))
                {
                    return new CuffableComponentState(CuffedHandCount,
                       CanStillInteract,
                       cuffs.CuffedRSI,
                       $"{cuffs.OverlayIconState}-{CuffedHandCount}",
                       cuffs.Color);
                    // the iconstate is formatted as blah-2, blah-4, blah-6, etc.
                    // the number corresponds to how many hands are cuffed.
                }
            }

            return new CuffableComponentState(CuffedHandCount,
               CanStillInteract,
               "/Objects/Misc/handcuffs.rsi",
               "body-overlay-2",
               Color.White);
        }

        /// <summary>
        /// Add a set of cuffs to an existing CuffedComponent.
        /// </summary>
        /// <param name="prototype"></param>
        public void AddNewCuffs(IEntity handcuff)
        {
            if (!handcuff.HasComponent<HandcuffComponent>())
            {
                Logger.Warning($"Handcuffs being applied to player are missing a {nameof(HandcuffComponent)}!");
                return;
            }

            if (!handcuff.InRangeUnobstructed(Owner, _interactRange))
            {
                Logger.Warning("Handcuffs being applied to player are obstructed or too far away! This should not happen!");
                return;
            }

            _container.Insert(handcuff);
            CanStillInteract = _hands.Hands.Count() > CuffedHandCount;

            OnCuffedStateChanged?.Invoke();
            UpdateAlert();
            UpdateHeldItems();
            Dirty();
        }

        /// <summary>
        /// Check the current amount of hands the owner has, and if there's less hands than active cuffs we remove some cuffs.
        /// </summary>
        private void UpdateHandCount()
        {
            var dirty = false;
            var handCount = _hands.Hands.Count();

            while (CuffedHandCount > handCount && CuffedHandCount > 0)
            {
                dirty = true;

                var entity = _container.ContainedEntities[_container.ContainedEntities.Count - 1];
                _container.Remove(entity);
                entity.Transform.WorldPosition = Owner.Transform.Coordinates.Position;
            }

            if (dirty)
            {
                CanStillInteract = handCount > CuffedHandCount;
                OnCuffedStateChanged.Invoke();
                Dirty();
            }
        }

        private void HandleHandCountChange(HandCountChangedEvent message)
        {
            if (message.Sender == Owner)
            {
                UpdateHandCount();
            }
        }

        /// <summary>
        /// Check how many items the user is holding and if it's more than the number of cuffed hands, drop some items.
        /// </summary>
        public void UpdateHeldItems()
        {
            var itemCount = _hands.GetAllHeldItems().Count();
            var freeHandCount = _hands.Hands.Count() - CuffedHandCount;

            if (freeHandCount < itemCount)
            {
                foreach (var item in _hands.GetAllHeldItems())
                {
                    if (freeHandCount < itemCount)
                    {
                        freeHandCount++;
                        _hands.Drop(item.Owner, false);
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Updates the status effect indicator on the HUD.
        /// </summary>
        private void UpdateAlert()
        {
            if (Owner.TryGetComponent(out ServerAlertsComponent status))
            {
                if (CanStillInteract)
                {
                    status.ClearAlert(AlertType.Handcuffed);
                }
                else
                {
                    status.ShowAlert(AlertType.Handcuffed);
                }
            }
        }

        /// <summary>
        /// Attempt to uncuff a cuffed entity. Can be called by the cuffed entity, or another entity trying to help uncuff them.
        /// If the uncuffing succeeds, the cuffs will drop on the floor.
        /// </summary>
        /// <param name="user">The cuffed entity</param>
        /// <param name="cuffsToRemove">Optional param for the handcuff entity to remove from the cuffed entity. If null, uses the most recently added handcuff entity.</param>
        public async void TryUncuff(IEntity user, IEntity cuffsToRemove = null)
        {
            var isOwner = user == Owner;

            if (cuffsToRemove == null)
            {
                cuffsToRemove = LastAddedCuffs;
            }
            else
            {
                if (!_container.ContainedEntities.Contains(cuffsToRemove))
                {
                    Logger.Warning("A user is trying to remove handcuffs that aren't in the owner's container. This should never happen!");
                }
            }

            if (!cuffsToRemove.TryGetComponent<HandcuffComponent>(out var cuff))
            {
                Logger.Warning($"A user is trying to remove handcuffs without a {nameof(HandcuffComponent)}. This should never happen!");
                return;
            }

            if (!isOwner && !ActionBlockerSystem.CanInteract(user))
            {
                user.PopupMessage(Loc.GetString("You can't do that!"));
                return;
            }

            if (!isOwner && !user.InRangeUnobstructed(Owner, _interactRange))
            {
                user.PopupMessage(Loc.GetString("You are too far away to remove the cuffs."));
                return;
            }

            if (!cuffsToRemove.InRangeUnobstructed(Owner, _interactRange))
            {
                Logger.Warning("Handcuffs being removed from player are obstructed or too far away! This should not happen!");
                return;
            }

            user.PopupMessage(Loc.GetString("You start removing the cuffs."));

            var audio = EntitySystem.Get<AudioSystem>();
            audio.PlayFromEntity(isOwner ? cuff.StartBreakoutSound : cuff.StartUncuffSound, Owner);

            var uncuffTime = isOwner ? cuff.BreakoutTime : cuff.UncuffTime;
            var doAfterEventArgs = new DoAfterEventArgs(user, uncuffTime)
            {
                BreakOnUserMove = true,
                BreakOnDamage = true,
                BreakOnStun = true,
                NeedHand = true
            };

            var doAfterSystem = EntitySystem.Get<DoAfterSystem>();
            var result = await doAfterSystem.DoAfter(doAfterEventArgs);

            if (result != DoAfterStatus.Cancelled)
            {
                audio.PlayFromEntity(cuff.EndUncuffSound, Owner);

                _container.ForceRemove(cuffsToRemove);
                cuffsToRemove.Transform.AttachToGridOrMap();
                cuffsToRemove.Transform.WorldPosition = Owner.Transform.WorldPosition;

                if (cuff.BreakOnRemove)
                {
                    cuff.Broken = true;

                    cuffsToRemove.Name = cuff.BrokenName;
                    cuffsToRemove.Description = cuff.BrokenDesc;

                    if (cuffsToRemove.TryGetComponent<SpriteComponent>(out var sprite))
                    {
                        sprite.LayerSetState(0, cuff.BrokenState); // TODO: safety check to see if RSI contains the state?
                    }
                }

                CanStillInteract = _hands.Hands.Count() > CuffedHandCount;
                OnCuffedStateChanged.Invoke();
                UpdateAlert();
                Dirty();

                if (CuffedHandCount == 0)
                {
                    user.PopupMessage(Loc.GetString("You successfully remove the cuffs."));

                    if (!isOwner)
                    {
                        user.PopupMessage(Owner, Loc.GetString("{0:theName} uncuffs your hands.", user));
                    }
                }
                else
                {
                    if (!isOwner)
                    {
                        user.PopupMessage(Loc.GetString("You successfully remove the cuffs. {0} of {1:theName}'s hands remain cuffed.", CuffedHandCount, user));
                        user.PopupMessage(Owner, Loc.GetString("{0:theName} removes your cuffs. {1} of your hands remain cuffed.", user, CuffedHandCount));
                    }
                    else
                    {
                        user.PopupMessage(Loc.GetString("You successfully remove the cuffs. {0} of your hands remain cuffed.", CuffedHandCount));
                    }
                }
            }
            else
            {
                user.PopupMessage(Loc.GetString("You fail to remove the cuffs."));
            }

            return;
        }

        /// <summary>
        /// Allows the uncuffing of a cuffed person. Used by other people and by the component owner to break out of cuffs.
        /// </summary>
        [Verb]
        private sealed class UncuffVerb : Verb<CuffableComponent>
        {
            protected override void GetData(IEntity user, CuffableComponent component, VerbData data)
            {
                if ((user != component.Owner && !ActionBlockerSystem.CanInteract(user)) || component.CuffedHandCount == 0)
                {
                    data.Visibility = VerbVisibility.Invisible;
                    return;
                }

                data.Text = Loc.GetString("Uncuff");
            }

            protected override void Activate(IEntity user, CuffableComponent component)
            {
                if (component.CuffedHandCount > 0)
                {
                    component.TryUncuff(user);
                }
            }
        }
    }
}
