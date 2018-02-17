﻿using Barotrauma.Networking;
using Barotrauma.Particles;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    partial class Character : Entity, IDamageable, ISerializableEntity, IClientSerializable, IServerSerializable
    {
        protected float soundTimer;
        protected float soundInterval;

        protected Vector2 LastHealthStatusVector;

        private List<CharacterSound> sounds;

        //the Character that the player is currently controlling
        private static Character controlled;

        private static Character spied;

        public static Character Spied
        {
            get { return spied; }
            set
            {
                if (spied == value) return;
                spied = value;
                CharacterHUD.Reset();

                if (controlled != null)
                {
                    controlled.Enabled = true;
                }
            }
        }

        public static Character Controlled
        {
            get { return controlled; }
            set
            {
                if (controlled == value) return;
                controlled = value;
                CharacterHUD.Reset();

                if (controlled != null)
                {
                    controlled.Enabled = true;
                }
            }
        }

        private Dictionary<object, HUDProgressBar> hudProgressBars;

        public Dictionary<object, HUDProgressBar> HUDProgressBars
        {
            get { return hudProgressBars; }
        }

        partial void InitProjSpecific(XDocument doc)
        {
            soundInterval = doc.Root.GetAttributeFloat("soundinterval", 10.0f);

            keys = new Key[Enum.GetNames(typeof(InputType)).Length];

            for (int i = 0; i < Enum.GetNames(typeof(InputType)).Length; i++)
            {
                keys[i] = new Key(GameMain.Config.KeyBind((InputType)i));
            }

            var soundElements = doc.Root.Elements("sound").ToList();

            sounds = new List<CharacterSound>();
            foreach (XElement soundElement in soundElements)
            {
                sounds.Add(new CharacterSound(soundElement));
            }

            hudProgressBars = new Dictionary<object, HUDProgressBar>();
        }


        public static void ViewSpied(float deltaTime, Camera cam, bool moveCam = true)
        {
            /*
            if (!DisableControls)
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i].SetState();
                }
            }
            else
            {
                foreach (Key key in keys)
                {
                    if (key == null) continue;
                    key.Reset();
                }
            }
            */

            if (moveCam)
            {
                if (Spied.needsAir &&
                    Spied.pressureProtection < 80.0f &&
                    (Spied.AnimController.CurrentHull == null || Spied.AnimController.CurrentHull.LethalPressure > 50.0f))
                {
                    float pressure = Spied.AnimController.CurrentHull == null ? 100.0f : Spied.AnimController.CurrentHull.LethalPressure;

                    cam.Zoom = MathHelper.Lerp(cam.Zoom,
                        (pressure / 50.0f) * Rand.Range(1.0f, 1.05f),
                        (pressure - 50.0f) / 50.0f);
                }

                if (Spied.IsHumanoid)
                {
                    if (!(Spied.SpeciesName.ToLowerInvariant() == "human") && GameMain.NilMod.UseCreatureZoomBoost)
                    {
                        cam.ZoomModifier = -0.10f;
                        cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, MathHelper.Clamp((300.0f * GameMain.NilMod.CreatureZoomMultiplier), 300f, 500f), deltaTime);
                    }
                    else
                    {
                        cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, 250.0f, deltaTime);
                    }
                }
                else
                {
                    float tempmass = Spied.Mass;
                    if (GameMain.NilMod.UseCreatureZoomBoost)
                    {
                        //increased visibility range when controlling large a non-humanoid
                        if ((tempmass) >= 1000)
                        {
                            tempmass = 1200f;
                            cam.ZoomModifier = -1f;
                        }
                        else if ((tempmass) >= 800)
                        {
                            tempmass = 1000f;
                            cam.ZoomModifier = -1f;
                        }
                        else if ((tempmass) >= 600)
                        {
                            tempmass = 800f;
                            cam.ZoomModifier = -1f;
                        }
                        else if ((tempmass) >= 400)
                        {
                            tempmass = 600f;
                            cam.ZoomModifier = -0.5f;
                        }
                        else if ((tempmass) >= 300)
                        {
                            tempmass = 500f;
                            cam.ZoomModifier = -0.4f;
                        }
                        else if ((tempmass) >= 200)
                        {
                            tempmass = 450f;
                            cam.ZoomModifier = -0.3f;
                        }
                        else if ((tempmass) >= 150)
                        {
                            tempmass = 400f;
                            cam.ZoomModifier = -0.3f;
                        }
                        else if ((tempmass) >= 100)
                        {
                            tempmass = 350f;
                            cam.ZoomModifier = -0.3f;
                        }
                        else if ((tempmass) >= 0)
                        {
                            tempmass = 300f;
                            cam.ZoomModifier = -0.3f;
                        }
                    }
                    cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, MathHelper.Clamp((tempmass * GameMain.NilMod.CreatureZoomMultiplier), 250.0f, 1600.0f), deltaTime);
                }
            }

            /*
            Spied.cursorPosition = cam.ScreenToWorld(PlayerInput.MousePosition);
            if (Spied.AnimController.CurrentHull != null && Spied.AnimController.CurrentHull.Submarine != null)
            {
                Spied.cursorPosition -= Spied.AnimController.CurrentHull.Submarine.Position;
            }

            Vector2 mouseSimPos = ConvertUnits.ToSimUnits(Spied.cursorPosition);
            

            if (Lights.LightManager.ViewTarget == Spied && Vector2.DistanceSquared(Spied.AnimController.Limbs[0].SimPosition, mouseSimPos) > 1.0f)
            {
                Body body = Submarine.PickBody(Spied.AnimController.Limbs[0].SimPosition, mouseSimPos);
                Structure structure = null;
                if (body != null) structure = body.UserData as Structure;
                if (structure != null)
                {
                    if (!structure.CastShadow && moveCam)
                    {
                        cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, 500.0f, 0.05f);
                    }
                }
            }
            */
        }

        /// <summary>
        /// Control the Character according to player input
        /// </summary>
        public void ControlLocalPlayer(float deltaTime, Camera cam, bool moveCam = true)
        {
            if (!DisableControls)
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i].SetState();
                }
            }
            else
            {
                foreach (Key key in keys)
                {
                    if (key == null) continue;
                    key.Reset();
                }
            }

            if (moveCam)
            {
                if (needsAir &&
                    pressureProtection < 80.0f &&
                    (AnimController.CurrentHull == null || AnimController.CurrentHull.LethalPressure > 50.0f))
                {
                    float pressure = AnimController.CurrentHull == null ? 100.0f : AnimController.CurrentHull.LethalPressure;

                    cam.Zoom = MathHelper.Lerp(cam.Zoom,
                        (pressure / 50.0f) * Rand.Range(1.0f, 1.05f),
                        (pressure - 50.0f) / 50.0f);
                }

                if (IsHumanoid)
                {
                    if(!(SpeciesName.ToLowerInvariant() == "human") && GameMain.NilMod.UseCreatureZoomBoost)
                    {
                        cam.ZoomModifier = -0.10f;
                        cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, MathHelper.Clamp((300.0f * GameMain.NilMod.CreatureZoomMultiplier),300f,500f), deltaTime);
                    }
                    else
                    {
                        cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, 250.0f, deltaTime);
                    }
                }
                else
                {
                    float tempmass = Mass;
                    if (GameMain.NilMod.UseCreatureZoomBoost)
                    {
                        //increased visibility range when controlling large a non-humanoid
                        if ((tempmass) >= 1000)
                        {
                            tempmass = 1200f;
                            cam.ZoomModifier = -1f;
                        }
                        else if ((tempmass) >= 800)
                        {
                            tempmass = 1000f;
                            cam.ZoomModifier = -1f;
                        }
                        else if ((tempmass) >= 600)
                        {
                            tempmass = 800f;
                            cam.ZoomModifier = -1f;
                        }
                        else if ((tempmass) >= 400)
                        {
                            tempmass = 600f;
                            cam.ZoomModifier = -0.5f;
                        }
                        else if ((tempmass) >= 300)
                        {
                            tempmass = 500f;
                            cam.ZoomModifier = -0.4f;
                        }
                        else if ((tempmass) >= 200)
                        {
                            tempmass = 450f;
                            cam.ZoomModifier = -0.3f;
                        }
                        else if ((tempmass) >= 150)
                        {
                            tempmass = 400f;
                            cam.ZoomModifier = -0.3f;
                        }
                        else if ((tempmass) >= 100)
                        {
                            tempmass = 350f;
                            cam.ZoomModifier = -0.3f;
                        }
                        else if ((tempmass) >= 0)
                        {
                            tempmass = 300f;
                            cam.ZoomModifier = -0.3f;
                        }
                    }
                    cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, MathHelper.Clamp((tempmass * GameMain.NilMod.CreatureZoomMultiplier), 250.0f, 1600.0f), deltaTime);
                }
            }

            cursorPosition = cam.ScreenToWorld(PlayerInput.MousePosition);
            if (AnimController.CurrentHull != null && AnimController.CurrentHull.Submarine != null)
            {
                cursorPosition -= AnimController.CurrentHull.Submarine.Position;
            }

            Vector2 mouseSimPos = ConvertUnits.ToSimUnits(cursorPosition);

            if (Lights.LightManager.ViewTarget == this && Vector2.DistanceSquared(AnimController.Limbs[0].SimPosition, mouseSimPos) > 1.0f)
            {
                Body body = Submarine.PickBody(AnimController.Limbs[0].SimPosition, mouseSimPos);
                Structure structure = null;
                if (body != null) structure = body.UserData as Structure;
                if (structure != null)
                {
                    if (!structure.CastShadow && moveCam)
                    {
                        cam.OffsetAmount = MathHelper.Lerp(cam.OffsetAmount, 500.0f, 0.05f);
                    }
                }
            }

            DoInteractionUpdate(deltaTime, mouseSimPos);

            DisableControls = false;
        }

        partial void UpdateControlled(float deltaTime, Camera cam)
        {
            if (controlled != this || spied != null) return;

            ControlLocalPlayer(deltaTime, cam);

            Lights.LightManager.ViewTarget = this;
            CharacterHUD.Update(deltaTime, this);

            foreach (HUDProgressBar progressBar in hudProgressBars.Values)
            {
                progressBar.Update(deltaTime);
            }

            foreach (var pb in hudProgressBars.Where(pb => pb.Value.FadeTimer <= 0.0f).ToList())
            {
                hudProgressBars.Remove(pb.Key);
            }
        }

        partial void DamageHUD(float amount)
        {
            if(spied == this) CharacterHUD.TakeDamage(amount);
            else if (controlled == this) CharacterHUD.TakeDamage(amount);
        }

        partial void UpdateOxygenProjSpecific(float prevOxygen)
        {
            if (prevOxygen > 0.0f && Oxygen <= 0.0f && controlled == this)
            {
                SoundPlayer.PlaySound("drown");
            }
        }

        partial void KillProjSpecific()
        {
            if (GameMain.NetworkMember != null && Character.controlled == this)
            {
                string chatMessage = InfoTextManager.GetInfoText("Self_CauseOfDeath." + causeOfDeath.ToString());
                if (GameMain.Client != null) chatMessage += " Your chat messages will only be visible to other dead players.";

                GameMain.NetworkMember.AddChatMessage(chatMessage, ChatMessageType.Dead);
                GameMain.LightManager.LosEnabled = false;
                controlled = null;
            }

            PlaySound(CharacterSound.SoundType.Die);
        }

        partial void DisposeProjSpecific()
        {
            if (controlled == this) controlled = null;

            if (GameMain.GameSession?.CrewManager != null &&
                GameMain.GameSession.CrewManager.GetCharacters().Contains(this))
            {
                GameMain.GameSession.CrewManager.RemoveCharacter(this);
            }

            if (GameMain.Client != null && GameMain.Client.Character == this) GameMain.Client.Character = null;

            if (Lights.LightManager.ViewTarget == this) Lights.LightManager.ViewTarget = null;
        }

        public static void AddAllToGUIUpdateList()
        {
            for (int i = 0; i < CharacterList.Count; i++)
            {
                CharacterList[i].AddToGUIUpdateList();
            }
        }

        public virtual void AddToGUIUpdateList()
        {
            if(spied == this)
            {
                CharacterHUD.AddToGUIUpdateList(this);
            }
            else if (spied == null && controlled == this)
            {
                CharacterHUD.AddToGUIUpdateList(this);
            }
        }

        public void Draw(SpriteBatch spriteBatch)
        {
            if (!Enabled) return;

            AnimController.Draw(spriteBatch);
        }

        public void DrawHUD(SpriteBatch spriteBatch, Camera cam)
        {
            CharacterHUD.Draw(spriteBatch, this, cam);
        }

        public virtual void DrawFront(SpriteBatch spriteBatch, Camera cam)
        {
            if (!Enabled) return;

            if (GameMain.DebugDraw)
            {
                AnimController.DebugDraw(spriteBatch);

                if (aiTarget != null) aiTarget.Draw(spriteBatch);
            }

            /*if (memPos != null && memPos.Count > 0 && controlled == this)
            {
                PosInfo serverPos = memPos.Last();
                Vector2 remoteVec = ConvertUnits.ToDisplayUnits(serverPos.Position);
                if (Submarine != null)
                {
                    remoteVec += Submarine.DrawPosition;
                }
                remoteVec.Y = -remoteVec.Y;

                PosInfo localPos = memLocalPos.Find(m => m.ID == serverPos.ID);
                int mpind = memLocalPos.FindIndex(lp => lp.ID == localPos.ID);
                PosInfo localPos1 = mpind > 0 ? memLocalPos[mpind - 1] : null;
                PosInfo localPos2 = mpind < memLocalPos.Count-1 ? memLocalPos[mpind + 1] : null;

                Vector2 localVec = ConvertUnits.ToDisplayUnits(localPos.Position);
                Vector2 localVec1 = localPos1 != null ? ConvertUnits.ToDisplayUnits(((PosInfo)localPos1).Position) : Vector2.Zero;
                Vector2 localVec2 = localPos2 != null ? ConvertUnits.ToDisplayUnits(((PosInfo)localPos2).Position) : Vector2.Zero;
                if (Submarine != null)
                {
                    localVec += Submarine.DrawPosition;
                    localVec1 += Submarine.DrawPosition;
                    localVec2 += Submarine.DrawPosition;
                }
                localVec.Y = -localVec.Y;
                localVec1.Y = -localVec1.Y;
                localVec2.Y = -localVec2.Y;

                //GUI.DrawLine(spriteBatch, remoteVec, localVec, Color.Yellow, 0, 10);
                if (localPos1 != null) GUI.DrawLine(spriteBatch, remoteVec, localVec1, Color.Lime, 0, 2);
                if (localPos2 != null) GUI.DrawLine(spriteBatch, remoteVec + Vector2.One, localVec2 + Vector2.One, Color.Red, 0, 2);
            }

            Vector2 mouseDrawPos = CursorWorldPosition;
            mouseDrawPos.Y = -mouseDrawPos.Y;
            GUI.DrawLine(spriteBatch, mouseDrawPos - new Vector2(0, 5), mouseDrawPos + new Vector2(0, 5), Color.Red, 0, 10);

            Vector2 closestItemPos = closestItem != null ? closestItem.DrawPosition : Vector2.Zero;
            closestItemPos.Y = -closestItemPos.Y;
            GUI.DrawLine(spriteBatch, closestItemPos - new Vector2(0, 5), closestItemPos + new Vector2(0, 5), Color.Lime, 0, 10);*/

            if (GUI.DisableHUD) return;

            Vector2 pos = DrawPosition;
            pos.Y = -pos.Y;

            if (speechBubbleTimer > 0.0f)
            {
                GUI.SpeechBubbleIcon.Draw(spriteBatch, pos - Vector2.UnitY * 100.0f,
                    speechBubbleColor * Math.Min(speechBubbleTimer, 1.0f), 0.0f,
                    Math.Min((float)speechBubbleTimer, 1.0f));
            }

            //if (this == controlled) return;

            if (info != null && !(this == controlled))
            {
                Vector2 namePos = new Vector2(pos.X, pos.Y - 90.0f - (5.0f / cam.Zoom)) - GUI.Font.MeasureString(Info.Name) * 0.5f / cam.Zoom;
                Color nameColor = Color.White;

                if (GameMain.NilMod.UseRecolouredNameInfo)
                {
                    if (Character.Controlled == null)
                    {
                        if (TeamID == 0)
                        {
                            if (!isDead)
                            {
                                nameColor = Color.White;
                            }
                            else
                            {
                                nameColor = Color.DarkGray;
                            }
                        }
                        else if (TeamID == 1)
                        {
                            if (!isDead)
                            {
                                nameColor = Color.LightBlue;
                            }
                            else
                            {
                                nameColor = Color.DarkBlue;
                            }
                        }
                        else if (TeamID == 2)
                        {
                            if (!isDead)
                            {
                                nameColor = Color.Red;
                            }
                            else
                            {
                                nameColor = Color.DarkRed;
                            }
                        }

                        //Im not really sure where to put this relative to teams and such so it can simply override all of them.
                        if (HuskInfectionState >= 0.5f)
                        {
                            nameColor = new Color(255, 100, 255, 255);
                        }
                    }

                    if (Character.Controlled != null)
                    {
                        if (TeamID == Character.Controlled.TeamID)
                        {
                            nameColor = Color.LightBlue;
                            if (IsDead)
                            {
                                nameColor = Color.DarkBlue;
                            }
                        }
                        if (TeamID != Character.Controlled.TeamID)
                        {
                            nameColor = Color.Red;
                            if (IsDead)
                            {
                                nameColor = Color.DarkRed;
                            }
                        }
                    }
                }
                else
                {
                    if (Character.Controlled != null)
                    {
                        if(TeamID != Character.Controlled.TeamID) nameColor = Color.Red;
                    }
                }

                GUI.Font.DrawString(spriteBatch, Info.Name, namePos + new Vector2(1.0f / cam.Zoom, 1.0f / cam.Zoom), Color.Black, 0.0f, Vector2.Zero, 1.0f / cam.Zoom, SpriteEffects.None, 0.001f);
                GUI.Font.DrawString(spriteBatch, Info.Name, namePos, nameColor, 0.0f, Vector2.Zero, 1.0f / cam.Zoom, SpriteEffects.None, 0.0f);

                if (GameMain.DebugDraw)
                {
                    GUI.Font.DrawString(spriteBatch, ID.ToString(), namePos - new Vector2(0.0f, 20.0f), Color.White);
                }
            }

            if (isDead) return;
            
            if (GameMain.NilMod.UseUpdatedCharHUD)
            {
                if ((health < maxHealth * 0.98f) || oxygen < 95f || bleeding >= 0.05f || (((AnimController.CurrentHull == null) ?
                    100.0f : Math.Min(AnimController.CurrentHull.LethalPressure, 100.0f)) > 10f && NeedsAir && PressureProtection == 0f) || HuskInfectionState > 0f || Stun > 0f)
                {
                    //Basic Colour
                    Color baseoutlinecolour;
                    //Basic Flash Colour if fine
                    Color FlashColour;
                    //Final Calculated outline colour
                    Color outLineColour;

                    //Negative Colours
                    Color NegativeLow = new Color(145, 145, 145, 160);
                    Color NegativeHigh = new Color(25, 25, 25, 220);

                    //Health Colours
                    Color HealthPositiveHigh = new Color(0, 255, 0, 15);
                    Color HealthPositiveLow = new Color(255, 0, 0, 60);
                    //Oxygen Colours
                    Color OxygenPositiveHigh = new Color(0, 255, 255, 15);
                    Color OxygenPositiveLow = new Color(0, 0, 200, 60);
                    //Stun Colours
                    Color StunPositiveHigh = new Color(235, 135, 45, 100);
                    Color StunPositiveLow = new Color(204, 119, 34, 30);
                    //Bleeding Colours
                    Color BleedPositiveHigh = new Color(255, 50, 50, 100);
                    Color BleedPositiveLow = new Color(150, 50, 50, 15);
                    //Pressure Colours
                    Color PressurePositiveHigh = new Color(255, 255, 0, 100);
                    Color PressurePositiveLow = new Color(125, 125, 0, 15);

                    //Husk Colours
                    Color HuskPositiveHigh = new Color(255, 100, 255, 150);
                    Color HuskPositiveLow = new Color(125, 30, 125, 15);

                    float pressureFactor = (AnimController.CurrentHull == null) ?
                    100.0f : Math.Min(AnimController.CurrentHull.LethalPressure, 100.0f);
                    if (PressureProtection > 0.0f && (WorldPosition.Y > GameMain.NilMod.PlayerCrushDepthOutsideHull || (WorldPosition.Y > GameMain.NilMod.PlayerCrushDepthInHull && CurrentHull != null))) pressureFactor = 0.0f;

                    if (IsRemotePlayer || this == Character.Controlled || AIController is HumanAIController)
                    {
                        //A players basic flash if ok
                        baseoutlinecolour = new Color(255, 255, 255, 15);
                        FlashColour = new Color(220, 220, 220, 15);

                        if (HuskInfectionState >= 1f) FlashColour = new Color(200, 0, 200, 255);
                        else if (HuskInfectionState > 0.5f) FlashColour = new Color(85, 0, 85, 150);
                        else if (HuskInfectionState > 0f) FlashColour = new Color(50, 0, 120, 50);
                        else if (pressureFactor > 80f) FlashColour = new Color(200, 200, 0, 100);
                        else if (bleeding > 0.75f) FlashColour = new Color(80, 30, 20, 100);
                        else if (pressureFactor > 45f) FlashColour = new Color(200, 200, 0, 100);
                        else if (oxygen < 50f) FlashColour = new Color(40, 40, 255, 40);
                        else if (health < 40f) FlashColour = new Color(25, 25, 25, 40);
                        else if (Stun >= 1f) FlashColour = new Color(5, 5, 5, 80);

                        if (IsUnconscious || Stun >= 5f) baseoutlinecolour = new Color(40, 40, 40, 35);
                    }
                    //Is an AI or well, not controlled by anybody, make their border different
                    else
                    {
                        baseoutlinecolour = new Color(40, 40, 40, 15);
                        FlashColour = new Color(5, 5, 5, 15);

                        //if (HuskInfectionState >= 2f) FlashColour = new Color(255, 0, 255, 255);
                        //else if (HuskInfectionState > 1f) FlashColour = new Color(200, 0, 200, 150);
                        //else if (HuskInfectionState > 0f) FlashColour = new Color(120, 0, 120, 100);
                        if (pressureFactor > 80f && NeedsAir) FlashColour = new Color(200, 200, 0, 100);
                        else if (bleeding > 1f) FlashColour = new Color(255, 10, 10, 100);
                        else if (pressureFactor > 45f && NeedsAir) FlashColour = new Color(200, 200, 0, 100);
                        else if (Stun >= 1f) FlashColour = new Color(10, 10, 10, 100);
                    }

                    if (GameMain.NilMod.CharFlashColourTime >= (NilMod.CharFlashColourRate / 2))
                    {
                        outLineColour = Color.Lerp(baseoutlinecolour, FlashColour, (GameMain.NilMod.CharFlashColourTime - (NilMod.CharFlashColourRate / 2)) / (NilMod.CharFlashColourRate / 2));
                    }
                    else
                    {
                        outLineColour = Color.Lerp(FlashColour, baseoutlinecolour, GameMain.NilMod.CharFlashColourTime / (NilMod.CharFlashColourRate / 2));
                    }

                    //Smooth out the Health bar movement a little c:
                    if (LastHealthStatusVector == null || LastHealthStatusVector == Vector2.Zero) LastHealthStatusVector = new Vector2(pos.X - 20f, DrawPosition.Y + 70.0f);
                    if ((LastHealthStatusVector.X + 40f) - DrawPosition.X > 2.0f || (LastHealthStatusVector.X + 40f) - DrawPosition.X < -2.0f || (LastHealthStatusVector.Y - 70f) - DrawPosition.Y > 2.0f || (LastHealthStatusVector.Y - 70f) - DrawPosition.Y < -2.0f) LastHealthStatusVector = new Vector2(pos.X - 40f, DrawPosition.Y + 70.0f);
                    Vector2 healthBarPos = LastHealthStatusVector;

                    //GUI.DrawProgressBar(spriteBatch, healthBarPos, new Vector2(100.0f, 10.0f), health / maxHealth, Color.Lerp(Color.Red, Color.Green, health / maxHealth) * 0.8f);

                    //Health Bar (Keep visible)
                    if (Health >= 0f)
                    {
                        if ((NeedsAir && oxygen > 85f) || !NeedsAir)
                        {
                            GUI.DrawProgressBar(spriteBatch, healthBarPos, new Vector2(80.0f, 20.0f), health / maxHealth, Color.Lerp(HealthPositiveLow, HealthPositiveHigh, health / maxHealth), outLineColour, 2f, 0, "Left");
                        }
                        else
                        {
                            GUI.DrawProgressBar(spriteBatch, healthBarPos, new Vector2(80.0f, 10.0f), health / maxHealth, Color.Lerp(HealthPositiveLow, HealthPositiveHigh, health / maxHealth), outLineColour, 2f, 0, "Left");
                        }
                    }
                    //Health has gone below 0
                    else
                    {
                        if ((NeedsAir && oxygen > 85f) || !NeedsAir)
                        {
                            GUI.DrawProgressBar(spriteBatch, healthBarPos, new Vector2(80.0f, 20.0f), -(health / maxHealth), Color.Lerp(NegativeLow, NegativeHigh, -(health / maxHealth)), outLineColour, 2f, 0, "Right");
                        }
                        else
                        {
                            GUI.DrawProgressBar(spriteBatch, healthBarPos, new Vector2(80.0f, 10.0f), -(health / maxHealth), Color.Lerp(NegativeLow, NegativeHigh, -(health / maxHealth)), outLineColour, 2f, 0, "Right");
                        }
                    }

                    //Oxygen Bar
                    if (NeedsAir && (oxygen <= 85f && oxygen >= 0f))
                    {
                        GUI.DrawProgressBar(spriteBatch, new Vector2(healthBarPos.X, healthBarPos.Y - 10f), new Vector2(80.0f, 10.0f), oxygen / 100f, Color.Lerp(OxygenPositiveLow, OxygenPositiveHigh, oxygen / 100f), outLineColour, 2f, 0f, "Left");
                    }
                    //Oxygen has gone below 0
                    else if (NeedsAir && oxygen < 0f)
                    {
                        GUI.DrawProgressBar(spriteBatch, new Vector2(healthBarPos.X, healthBarPos.Y - 10f), new Vector2(80.0f, 10.0f), -(oxygen / 100f), Color.Lerp(NegativeLow, NegativeHigh, -(oxygen / 100f)), outLineColour, 2f, 0f, "Right");
                    }

                    //Stun Bar
                    if (Stun > 1.0f && !IsUnconscious)
                    {
                        GUI.DrawProgressBar(spriteBatch, new Vector2(healthBarPos.X, healthBarPos.Y - 20f), new Vector2(80.0f, 10.0f), Stun / 60f, Color.Lerp(StunPositiveLow, StunPositiveHigh, Stun / 60f), outLineColour, 2f, 0f, "Left");
                    }

                    //Bleed Bar
                    if (bleeding > 0.0f)
                    {
                        GUI.DrawProgressBar(spriteBatch, new Vector2(healthBarPos.X, healthBarPos.Y + 10f), new Vector2(40.0f, 10.0f), bleeding / 5f, Color.Lerp(BleedPositiveLow, BleedPositiveHigh, bleeding / 5f), outLineColour, 2f, 0f, "Left");
                    }
                    //Pressure Bar
                    if (pressureFactor > 0.0f)
                    {
                        GUI.DrawProgressBar(spriteBatch, new Vector2(healthBarPos.X + 40f, healthBarPos.Y + 10f), new Vector2(40.0f, 10.0f), pressureFactor / 100f, Color.Lerp(PressurePositiveLow, PressurePositiveHigh, pressureFactor / 100f), outLineColour, 2f, 0f, "Right");
                    }
                    //Husk Bar
                    if (HuskInfectionState > 0.0f)
                    {
                        GUI.DrawProgressBar(spriteBatch, new Vector2(healthBarPos.X + 80f, healthBarPos.Y - ((pressureFactor > 0.0f) ? 10.0f : 0.0f)), new Vector2(20.0f, (pressureFactor > 0.0f) ? 30.0f : 20.0f), HuskInfectionState, Color.Lerp(HuskPositiveLow, HuskPositiveHigh, HuskInfectionState), outLineColour, 2f, 0f, "Bottom");
                    }
                }
            }
            else
            {
                if (health < maxHealth * 0.98f)
                {
                    Vector2 healthBarPos = new Vector2(pos.X - 50, DrawPosition.Y + 100.0f);

                    GUI.DrawProgressBar(spriteBatch, healthBarPos, new Vector2(100.0f, 15.0f), health / maxHealth, Color.Lerp(Color.Red, Color.Green, health / maxHealth) * 0.8f);
                }
            }
        }

        /// <summary>
        /// Creates a progress bar that's "linked" to the specified object (or updates an existing one if there's one already linked to the object)
        /// The progress bar will automatically fade out after 1 sec if the method hasn't been called during that time
        /// </summary>
        public HUDProgressBar UpdateHUDProgressBar(object linkedObject, Vector2 worldPosition, float progress, Color emptyColor, Color fullColor)
        {
            if (controlled != this) return null;

            HUDProgressBar progressBar = null;
            if (!hudProgressBars.TryGetValue(linkedObject, out progressBar))
            {
                progressBar = new HUDProgressBar(worldPosition, Submarine, emptyColor, fullColor);
                hudProgressBars.Add(linkedObject, progressBar);
            }

            progressBar.WorldPosition = worldPosition;
            progressBar.FadeTimer = Math.Max(progressBar.FadeTimer, 1.0f);
            progressBar.Progress = progress;

            return progressBar;
        }

        public void PlaySound(CharacterSound.SoundType soundType)
        {
            if (sounds == null || sounds.Count == 0) return;

            var matchingSounds = sounds.FindAll(s => s.Type == soundType);
            if (matchingSounds.Count == 0) return;

            var selectedSound = matchingSounds[Rand.Int(matchingSounds.Count)];
            selectedSound.Sound.Play(1.0f, selectedSound.Range, AnimController.WorldPosition);
        }

        partial void ImplodeFX()
        {
            Vector2 centerOfMass = AnimController.GetCenterOfMass();

            SoundPlayer.PlaySound("implode", 1.0f, 150.0f, WorldPosition);

            for (int i = 0; i < 10; i++)
            {
                Particle p = GameMain.ParticleManager.CreateParticle("waterblood",
                    ConvertUnits.ToDisplayUnits(centerOfMass) + Rand.Vector(5.0f),
                    Rand.Vector(10.0f));
                if (p != null) p.Size *= 2.0f;

                GameMain.ParticleManager.CreateParticle("bubbles",
                    ConvertUnits.ToDisplayUnits(centerOfMass) + Rand.Vector(5.0f),
                    new Vector2(Rand.Range(-50f, 50f), Rand.Range(-100f, 50f)));
            }
        }
    }
}
