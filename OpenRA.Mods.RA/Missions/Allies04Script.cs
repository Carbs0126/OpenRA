#region Copyright & License Information
/*
 * Copyright 2007-2012 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation. For more information,
 * see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OpenRA.FileFormats;
using OpenRA.Mods.RA.Activities;
using OpenRA.Mods.RA.Air;
using OpenRA.Mods.RA.Buildings;
using OpenRA.Mods.RA.Render;
using OpenRA.Network;
using OpenRA.Traits;

namespace OpenRA.Mods.RA.Missions
{
	class Allies04ScriptInfo : TraitInfo<Allies04Script>, Requires<SpawnMapActorsInfo> { }

	class Allies04Script : IHasObjectives, IWorldLoaded, ITick
	{
		public event ObjectivesUpdatedEventHandler OnObjectivesUpdated = notify => { };

		public IEnumerable<Objective> Objectives { get { return objectives.Values; } }

		Dictionary<int, Objective> objectives = new Dictionary<int, Objective>
		{
			{ InfiltrateID, new Objective(ObjectiveType.Primary, "", ObjectiveStatus.InProgress) },
			{ DestroyID, new Objective(ObjectiveType.Primary, "Secure the Soviet research laboratory and destroy the rest of the Soviet base.", ObjectiveStatus.Inactive) }
		};

		const int InfiltrateID = 0;
		const int DestroyID = 1;
		const string Infiltrate = "The Soviets are currently developing a new defensive system named the \"Iron Curtain\" at their main research laboratories. Get our {0} into the Soviet research laboratories undetected.";

		Actor lstEntryPoint;
		Actor lstUnloadPoint;
		Actor lstExitPoint;
		Actor hijackTruck;
		Actor baseGuard;
		Actor baseGuardMovePos;
		Actor baseGuardTruckPos;
		Actor lab;
		int baseGuardTicks = 100;

		Actor allies1Spy;
		Actor allies2Spy;
		bool allies1SpyInfiltratedLab;
		bool allies2SpyInfiltratedLab;
		int frameInfiltrated = -1;

		Player allies;
		Player allies1;
		Player allies2;
		Player soviets;
		World world;

		static readonly string[] DogPatrol = { "e1", "dog.patrol", "dog.patrol" };
		static readonly string[] InfantryPatrol = { "e1", "e1", "e1", "e1", "e1" };

		Actor[] patrol1;
		CPos[] patrolPoints1;
		int currentPatrolPoint1;
		Actor[] patrol2;
		CPos[] patrolPoints2;
		int currentPatrolPoint2 = 3;
		Actor[] patrol3;
		CPos[] patrolPoints3;
		int currentPatrolPoint3;
		Actor[] patrol4;
		CPos[] patrolPoints4;
		int currentPatrolPoint4;
		Actor[] patrol5;
		CPos[] patrolPoints5;
		int currentPatrolPoint5;

		CPos hind1EntryPoint;
		PPos[] hind1Points;
		CPos hind1ExitPoint;

		Actor reinforcementsEntryPoint;
		Actor reinforcementsUnloadPoint;

		void MissionFailed(string text)
		{
			if (allies1.WinState != WinState.Undefined)
			{
				return;
			}
			allies1.WinState = allies2.WinState = WinState.Lost;
			foreach (var actor in world.Actors.Where(a => a.IsInWorld && (a.Owner == allies1 || a.Owner == allies2) && !a.IsDead()))
			{
				actor.Kill(actor);
			}
			Game.AddChatLine(Color.Red, "Mission failed", text);
			Sound.Play("misnlst1.aud");
		}

		void MissionAccomplished(string text)
		{
			if (allies1.WinState != WinState.Undefined)
			{
				return;
			}
			allies1.WinState = allies2.WinState = WinState.Won;
			Game.AddChatLine(Color.Blue, "Mission accomplished", text);
			Sound.Play("misnwon1.aud");
		}

		public void Tick(Actor self)
		{
			if (allies1.WinState != WinState.Undefined)
			{
				return;
			}
			if (world.FrameNumber == 1)
			{
				InsertSpies();
			}
			if (world.FrameNumber == 600)
			{
				SendHind(hind1EntryPoint, hind1Points, hind1ExitPoint);
			}
			if (frameInfiltrated != -1 && world.FrameNumber == frameInfiltrated + 100)
			{
				Sound.Play("aarrivs1.aud");
				world.AddFrameEndTask(w => SendReinforcements());
			}
			PatrolTick(ref patrol1, ref currentPatrolPoint1, soviets, DogPatrol, patrolPoints1);
			PatrolTick(ref patrol2, ref currentPatrolPoint2, soviets, InfantryPatrol, patrolPoints2);
			PatrolTick(ref patrol3, ref currentPatrolPoint3, soviets, DogPatrol, patrolPoints3);
			PatrolTick(ref patrol4, ref currentPatrolPoint4, soviets, DogPatrol, patrolPoints4);
			PatrolTick(ref patrol5, ref currentPatrolPoint5, soviets, DogPatrol, patrolPoints5);
			ManageSovietOre();
			BaseGuardTick();
			if (allies1Spy.IsDead() || (allies2Spy != null && allies2Spy.IsDead()))
			{
				objectives[InfiltrateID].Status = ObjectiveStatus.Failed;
				OnObjectivesUpdated(true);
				MissionFailed("{0} spy was killed.".F(allies1 != allies2 ? "A" : "The"));
			}
			else if (lab.Destroyed)
			{
				MissionFailed("The research laboratory was destroyed.");
			}
			else if (!world.Actors.Any(a => (a.Owner == allies1 || a.Owner == allies2) && !a.IsDead() && (a.HasTrait<Building>() && !a.HasTrait<Wall>()) || a.HasTrait<BaseBuilding>()))
			{
				objectives[DestroyID].Status = ObjectiveStatus.Failed;
				OnObjectivesUpdated(true);
				MissionFailed("The remaining Allied forces in the area have been wiped out.");
			}
			else if (!world.Actors.Any(a => a.Owner == soviets && a.IsInWorld && !a.IsDead() && a.HasTrait<Building>() && !a.HasTrait<Wall>() && a != lab)
				&& objectives[InfiltrateID].Status == ObjectiveStatus.Completed)
			{
				objectives[DestroyID].Status = ObjectiveStatus.Completed;
				OnObjectivesUpdated(true);
				MissionAccomplished("The Soviet research laboratory has been secured successfully.");
			}
		}

		void ManageSovietOre()
		{
			var res = soviets.PlayerActor.Trait<PlayerResources>();
			res.TakeOre(res.Ore);
			res.TakeCash(res.Cash);
		}

		void BaseGuardTick()
		{
			if (baseGuardTicks <= 0 || baseGuard.IsDead() || !baseGuard.IsInWorld)
			{
				return;
			}
			if (hijackTruck.Location == baseGuardTruckPos.Location)
			{
				if (--baseGuardTicks <= 0)
				{
					baseGuard.QueueActivity(new Move.Move(baseGuardMovePos.Location));
				}
			}
			else
			{
				baseGuardTicks = 100;
			}
		}

		void OnLabInfiltrated(Actor spy)
		{
			if (spy == allies1Spy) { allies1SpyInfiltratedLab = true; }
			else if (spy == allies2Spy) { allies2SpyInfiltratedLab = true; }
			if (allies1SpyInfiltratedLab && (allies2SpyInfiltratedLab || allies2Spy == null))
			{
				objectives[InfiltrateID].Status = ObjectiveStatus.Completed;
				objectives[DestroyID].Status = ObjectiveStatus.InProgress;
				OnObjectivesUpdated(true);
				frameInfiltrated = world.FrameNumber;
			}
		}

		void SendReinforcements()
		{
			var lst = world.CreateActor("lst", new TypeDictionary 
			{ 
				new OwnerInit(allies1),
				new LocationInit(reinforcementsEntryPoint.Location)
			});
			lst.Trait<Cargo>().Load(lst, world.CreateActor(false, "mcv", new TypeDictionary { new OwnerInit(allies1) } ));
			if (allies1 != allies2)
			{
				lst.Trait<Cargo>().Load(lst, world.CreateActor(false, "mcv", new TypeDictionary { new OwnerInit(allies2) }));
			}
			lst.QueueActivity(new Move.Move(reinforcementsUnloadPoint.Location));
			lst.QueueActivity(new Wait(10));
			lst.QueueActivity(new UnloadCargo(true));
			lst.QueueActivity(new Wait(10));
			lst.QueueActivity(new Move.Move(reinforcementsEntryPoint.Location));
			lst.QueueActivity(new RemoveSelf());
		}

		void PatrolTick(ref Actor[] patrolActors, ref int currentPoint, Player owner, string[] actorNames, CPos[] points)
		{
			if (patrolActors == null)
			{
				var td = new TypeDictionary { new OwnerInit(owner), new LocationInit(points[currentPoint]) };
				patrolActors = actorNames.Select(f => world.CreateActor(f, td)).ToArray();
			}
			var leader = patrolActors[0];
			if (!leader.IsDead() && leader.IsIdle && leader.IsInWorld)
			{
				currentPoint = (currentPoint + 1) % points.Length;
				leader.QueueActivity(new AttackMove.AttackMoveActivity(leader, new Move.Move(points[currentPoint], 0)));
				leader.QueueActivity(new Wait(50));
				foreach (var follower in patrolActors.Skip(1))
				{
					follower.QueueActivity(new Wait(world.SharedRandom.Next(0, 25)));
					follower.QueueActivity(new AttackMove.AttackMoveActivity(follower, new Move.Move(points[currentPoint], 0)));
				}
			}
		}

		void SendHind(CPos start, IEnumerable<PPos> points, CPos exit)
		{
			var hind = world.CreateActor("hind", new TypeDictionary
			{
				new OwnerInit(soviets),
				new LocationInit(start),
				new FacingInit(Util.GetFacing(points.First().ToCPos() - start, 0)),
				new AltitudeInit(Rules.Info["hind"].Traits.Get<HelicopterInfo>().CruiseAltitude),
			});
			foreach (var point in points.Concat(new[] { Util.CenterOfCell(exit) }))
			{
				hind.QueueActivity(new AttackMove.AttackMoveActivity(hind, new HeliFly(point)));
			}
			hind.QueueActivity(new RemoveSelf());
		}

		void InsertSpies()
		{
			var lst = world.CreateActor("lst", new TypeDictionary 
			{ 
				new OwnerInit(allies1),
				new LocationInit(lstEntryPoint.Location)
			});
			allies1Spy = world.CreateActor(false, "spy", new TypeDictionary { new OwnerInit(allies1) });
			lst.Trait<Cargo>().Load(lst, allies1Spy);
			if (allies1 != allies2)
			{
				allies2Spy = world.CreateActor(false, "spy", new TypeDictionary { new OwnerInit(allies2) });
				lst.Trait<Cargo>().Load(lst, allies2Spy);
			}
			lst.QueueActivity(new Move.Move(lstUnloadPoint.Location));
			lst.QueueActivity(new Wait(10));
			lst.QueueActivity(new UnloadCargo(true));
			lst.QueueActivity(new Wait(10));
			lst.QueueActivity(new Move.Move(lstExitPoint.Location));
			lst.QueueActivity(new RemoveSelf());
		}

		void SetupSubStances()
		{
			if (!Game.IsHost)
			{
				return;
			}
			foreach (var actor in world.Actors.Where(a => a.IsInWorld && a.Owner == soviets && !a.IsDead() && a.HasTrait<TargetableSubmarine>()))
			{
				world.IssueOrder(new Order("SetUnitStance", actor, false)
				{
					TargetLocation = new CPos((int)UnitStance.Defend, 0)
				});
			}
		}

		public void WorldLoaded(World w)
		{
			world = w;
			allies1 = w.Players.Single(p => p.InternalName == "Allies1");
			allies2 = w.Players.SingleOrDefault(p => p.InternalName == "Allies2");
			if (allies2 == null)
			{
				allies2 = allies1;
			}
			allies = w.Players.Single(p => p.InternalName == "Allies");
			soviets = w.Players.Single(p => p.InternalName == "Soviets");
			var actors = w.WorldActor.Trait<SpawnMapActors>().Actors;
			lstEntryPoint = actors["LstEntryPoint"];
			lstUnloadPoint = actors["LstUnloadPoint"];
			lstExitPoint = actors["LstExitPoint"];
			hijackTruck = actors["HijackTruck"];
			baseGuard = actors["BaseGuard"];
			baseGuardMovePos = actors["BaseGuardMovePos"];
			baseGuardTruckPos = actors["BaseGuardTruckPos"];
			patrolPoints1 = new[]
			{
				actors["PatrolPoint11"].Location,
				actors["PatrolPoint12"].Location,
				actors["PatrolPoint13"].Location,
				actors["PatrolPoint14"].Location,
				actors["PatrolPoint15"].Location
			};
			patrolPoints2 = patrolPoints1;
			patrolPoints3 = new[]
			{
				actors["PatrolPoint21"].Location,
				actors["PatrolPoint22"].Location,
				actors["PatrolPoint23"].Location,
				actors["PatrolPoint24"].Location,
				actors["PatrolPoint25"].Location
			};
			patrolPoints4 = new[]
			{
				actors["PatrolPoint31"].Location,
				actors["PatrolPoint32"].Location,
				actors["PatrolPoint33"].Location,
				actors["PatrolPoint34"].Location
			};
			patrolPoints5 = new[]
			{
				actors["PatrolPoint41"].Location,
				actors["PatrolPoint42"].Location,
				actors["PatrolPoint43"].Location,
				actors["PatrolPoint44"].Location,
				actors["PatrolPoint45"].Location
			};
			lab = actors["Lab"];
			lab.AddTrait(new Allies04InfiltrateAction(OnLabInfiltrated));
			hind1EntryPoint = actors["Hind1EntryPoint"].Location;
			hind1Points = new[]
			{
				actors["Hind1Point1"].CenterLocation,
				actors["Hind1Point2"].CenterLocation
			};
			hind1ExitPoint = actors["Hind1ExitPoint"].Location;
			reinforcementsEntryPoint = actors["ReinforcementsEntryPoint"];
			reinforcementsUnloadPoint = actors["ReinforcementsUnloadPoint"];
			objectives[InfiltrateID].Text = Infiltrate.F(allies1 != allies2 ? "spies" : "spy");
			OnObjectivesUpdated(false);
			SetupSubStances();
			Game.MoveViewport(lstEntryPoint.Location.ToFloat2());
			PlayMusic();
			Game.ConnectionStateChanged += StopMusic;
		}

		void PlayMusic()
		{
			if (!Rules.InstalledMusic.Any())
			{
				return;
			}
			var track = Rules.InstalledMusic.Random(Game.CosmeticRandom);
			Sound.PlayMusicThen(track.Value, PlayMusic);
		}

		void StopMusic(OrderManager orderManager)
		{
			if (!orderManager.GameStarted)
			{
				Sound.StopMusic();
				Game.ConnectionStateChanged -= StopMusic;
			}
		}
	}

	class Allies04HijackableInfo : ITraitInfo
	{
		public object Create(ActorInitializer init) { return new Allies04Hijackable(init.self); }
	}

	class Allies04Hijackable : IAcceptSpy, INotifyPassengerExited
	{
		public Player OldOwner;

		public Allies04Hijackable(Actor self)
		{
			OldOwner = self.Owner;
		}

		public void OnInfiltrate(Actor self, Actor spy)
		{
			if (self.Trait<Cargo>().IsEmpty(self))
			{
				self.ChangeOwner(spy.Owner);
			}
			self.Trait<Cargo>().Load(self, spy);
		}

		public void PassengerExited(Actor self, Actor passenger)
		{
			if (self.Trait<Cargo>().IsEmpty(self))
			{
				self.CancelActivity();
				self.ChangeOwner(OldOwner);
			}
			else if (self.Owner == passenger.Owner)
			{
				self.ChangeOwner(self.Trait<Cargo>().Passengers.First().Owner);
			}
		}
	}

	class Allies04RenderHijackedInfo : RenderUnitInfo
	{
		public override object Create(ActorInitializer init) { return new Allies04RenderHijacked(init.self, this); }
	}

	class Allies04RenderHijacked : RenderUnit, IRenderModifier
	{
		Allies04Hijackable hijackable;

		public Allies04RenderHijacked(Actor self, Allies04RenderHijackedInfo info)
			: base(self)
		{
			hijackable = self.Trait<Allies04Hijackable>();
		}

		public IEnumerable<Renderable> ModifyRender(Actor self, IEnumerable<Renderable> r)
		{
			return r.Select(a => a.WithPalette(Palette(hijackable.OldOwner)));
		}
	}

	class Allies04InfiltrateAction : IAcceptSpy
	{
		Action<Actor> a;

		public Allies04InfiltrateAction(Action<Actor> a)
		{
			this.a = a;
		}

		public void OnInfiltrate(Actor self, Actor spy)
		{
			a(spy);
		}
	}
}
