﻿using System;
using System.Collections.Generic;
using System.Numerics;

using theori;
using theori.Graphics;

using OpenGL;

using OpenRM;
using OpenRM.Voltex;
using theori.Resources;

namespace NeuroSonic.GamePlay
{
    public class HighwayView
    {
        struct KeyBeamInfo
        {
            public float Alpha;
            public Vector3 Color;
        }

        struct GlowInfo
        {
            public OpenRM.Object Object;
            public float Glow;
            public int GlowState;
        }

        //private const float PITCH_AMT = 15;
        private const float LENGTH_BASE = 11;
        private const float LENGTH_ADD = 1.1f;

        private float roll;
        private float m_pitch, m_zoom; // "top", "bottom"
        public float CritScreenY = 0.1f;

        public readonly BasicCamera Camera;
        public Transform DefaultTransform { get; private set; }
        public Transform DefaultZoomedTransform { get; private set; }
        public Transform WorldTransform { get; private set; }
        public Transform CritLineTransform { get; private set; }

        private readonly ClientResourceManager m_skin;

        private readonly Drawable3D m_highwayDrawable;
        private readonly Drawable3D[] m_keyBeamDrawables = new Drawable3D[6];
        private readonly Drawable3D m_lVolEntryDrawable, m_lVolExitDrawable;
        private readonly Drawable3D m_rVolEntryDrawable, m_rVolExitDrawable;

        private Vector3 m_lVolColor, m_rVolColor;

        private Dictionary<OpenRM.Object, ObjectRenderable3D>[] m_renderables = new Dictionary<OpenRM.Object, ObjectRenderable3D>[8];
        private readonly KeyBeamInfo[] m_keyBeamInfos = new KeyBeamInfo[6];
        private readonly GlowInfo[] m_glowInfos = new GlowInfo[8];
        private readonly bool[] m_streamsActive = new bool[8].Fill(true);

        public time_t PlaybackPosition { get; set; }

        public time_t ViewDuration { get; set; }

        public float LaserRoll => roll;
        public float CriticalHeight => (1 - CritScreenY) * Camera.ViewportHeight;

        public float HorizonHeight { get; private set; }

        public float TargetLaserRoll { get; set; }
        public float TargetBaseRoll { get; set; }
        public float TargetEffectRoll { get; set; }

        public float TargetPitch { get; set; }
        public float TargetZoom { get; set; }
        public float TargetOffset { get; set; }
        public float TargetEffectOffset { get; set; }

        public Vector3 CameraOffset { get; set; }
        
        const float SLAM_DUR_TICKS = 1 / 32.0f;
        time_t SlamDurationTime(OpenRM.Object obj) => obj.Chart.ControlPoints.MostRecent(obj.Position).MeasureDuration * SLAM_DUR_TICKS;

        public HighwayView(ClientResourceManager skin)
        {
            m_skin = skin;

            m_lVolColor = Color.HSVtoRGB(new Vector3(Plugin.Config.GetInt(NscConfigKey.Laser0Color) / 360.0f, 1, 1));
            m_rVolColor = Color.HSVtoRGB(new Vector3(Plugin.Config.GetInt(NscConfigKey.Laser1Color) / 360.0f, 1, 1));

            var highwayParams = new MaterialParams();
            highwayParams["LeftColor"] = m_lVolColor;
            highwayParams["RightColor"] = m_rVolColor;
            highwayParams["Hidden"] = 0.0f;

            var basicMaterial = m_skin.AquireMaterial("materials/basic");
            var lVolMaterial = m_skin.AquireMaterial("materials/laser");
            lVolMaterial.BlendMode = BlendMode.Additive;
            var rVolMaterial = m_skin.AquireMaterial("materials/laser");
            rVolMaterial.BlendMode = BlendMode.Additive;

            var keyBeamTexture = m_skin.AquireTexture("textures/key_beam");
            var keyBeamMesh = Mesh.CreatePlane(Vector3.UnitX, Vector3.UnitZ, 1, LENGTH_BASE + LENGTH_ADD, Anchor.BottomCenter);

            m_highwayDrawable = new Drawable3D()
            {
                Texture = m_skin.AquireTexture("textures/highway"),
                Material = m_skin.AquireMaterial("materials/highway"),
                Mesh = Mesh.CreatePlane(Vector3.UnitX, Vector3.UnitZ, 1, LENGTH_BASE + LENGTH_ADD, Anchor.BottomCenter),
                Params = highwayParams,
            };

            for (int i = 0; i < 6; i++)
            {
                m_keyBeamDrawables[i] = new Drawable3D()
                {
                    Texture = keyBeamTexture,
                    Mesh = keyBeamMesh,
                    Material = basicMaterial,
                };
            }

            MaterialParams CreateVolumeParams(int lane)
            {
                var volParams = new MaterialParams();
                volParams["LaserColor"] = lane == 0 ? m_lVolColor : m_rVolColor;
                volParams["HiliteColor"] = new Vector3(1, 1, 0);
                return volParams;
            }

            void CreateVolDrawables(int lane, ref Drawable3D entryDrawable, ref Drawable3D exitDrawable)
            {
                // TODO(local): use the lane value!
                var entryTexture = m_skin.AquireTexture("textures/laser_entry");
                var exitTexture = m_skin.AquireTexture("textures/laser_exit");

                entryDrawable = new Drawable3D()
                {
                    Texture = entryTexture,
                    Mesh = Mesh.CreatePlane(Vector3.UnitX, Vector3.UnitZ, 1 / 6.0f, (entryTexture.Height / (float)entryTexture.Width) / 6.0f, Anchor.TopCenter),
                    Material = lane == 0 ? lVolMaterial : rVolMaterial,
                    Params = CreateVolumeParams(lane),
                };

                exitDrawable = new Drawable3D()
                {
                    Texture = exitTexture,
                    Mesh = Mesh.CreatePlane(Vector3.UnitX, Vector3.UnitZ, 1 / 6.0f, (entryTexture.Height / (float)entryTexture.Width) / 6.0f, Anchor.BottomCenter),
                    Material = lane == 0 ? lVolMaterial : rVolMaterial,
                    Params = CreateVolumeParams(lane),
                };
            }

            CreateVolDrawables(0, ref m_lVolEntryDrawable, ref m_lVolExitDrawable);
            CreateVolDrawables(1, ref m_rVolEntryDrawable, ref m_rVolExitDrawable);

            Camera = new BasicCamera();
            Camera.SetPerspectiveFoV(60, Window.Aspect, 0.01f, 1000);
            
            m_renderables.Fill(() => new Dictionary<OpenRM.Object, ObjectRenderable3D>());
        }

        public void Reset()
        {
            foreach (var r in m_renderables)
                r.Clear();
        }

        public void RenderableObjectAppear(OpenRM.Object obj)
        {
            if (obj.Stream >= 8) return;

            if (obj is ButtonObject bobj)
            {
                ObjectRenderable3D br3d;
                if (obj.IsInstant)
                    br3d = new ButtonChipRenderState3D(bobj, m_skin);
                else
                {
                    float zDur = (float)(obj.AbsoluteDuration.Seconds / ViewDuration.Seconds);
                    br3d = new ButtonHoldRenderState3D(bobj, zDur * LENGTH_BASE, m_skin);
                }

                m_renderables[obj.Stream][obj] = br3d;
            }
            else if (obj is AnalogObject aobj)
            {
                var color = obj.Stream == 6 ? m_lVolColor : m_rVolColor;

                if (obj.IsInstant)
                {
                    float zDur = (float)(SlamDurationTime(aobj).Seconds / ViewDuration.Seconds);
                    m_renderables[obj.Stream][obj] = new SlamRenderState3D(aobj, zDur * LENGTH_BASE, color, m_skin);
                }
                else
                {
                    time_t duration = obj.AbsoluteDuration;
                    if (aobj.PreviousConnected != null && aobj.Previous.IsInstant)
                        duration -= SlamDurationTime(aobj.PreviousConnected);

                    float zDur = (float)(duration.Seconds / ViewDuration.Seconds);
                    m_renderables[obj.Stream][obj] = new LaserRenderState3D(aobj, zDur * LENGTH_BASE, color, m_skin);
                }
            }
        }

        public void RenderableObjectDisappear(OpenRM.Object obj)
        {
            if (obj.Stream >= 8) return;
            m_renderables[obj.Stream].Remove(obj);
        }

        public void CreateKeyBeam(int lane, Vector3 color)
        {
            m_keyBeamInfos[lane].Alpha = 1.0f;
            m_keyBeamInfos[lane].Color = color;
        }

        public void SetStreamActive(int stream, bool active)
        {
            m_streamsActive[stream] = active;
        }

        public void SetObjectGlow(OpenRM.Object targetObject, float glow, int glowState)
        {
            ref GlowInfo glowInfo = ref m_glowInfos[targetObject.Stream];
            glowInfo.Object = targetObject;
            glowInfo.Glow = glow;
            glowInfo.GlowState = glowState;

            Logger.Log($"({ targetObject.Stream }) { glowState }");
        }

        public void Update()
        {
            for (int i = 0; i < 6; i++)
            {
                const float KEY_BEAM_SPEED = 10.0f;
                m_keyBeamInfos[i].Alpha = Math.Max(0, m_keyBeamInfos[i].Alpha - Time.Delta * KEY_BEAM_SPEED);
            }

            Camera.ViewportWidth = Window.Width;
            Camera.ViewportHeight = Window.Height;

            roll = TargetLaserRoll;
            m_pitch = TargetPitch;
            m_zoom = TargetZoom;
            
            Transform GetAtRoll(float roll, float xOffset)
            {
                //const float ANCHOR_Y = -0.825f;
                //const float CONTNR_Z = -1.1f;
                
                const float ANCHOR_ROT = 2.5f;
                const float ANCHOR_Y = -0.7925f;
                const float CONTNR_Z = -0.975f;

                var origin = Transform.RotationZ(roll);
                var anchor = Transform.RotationX(ANCHOR_ROT)
                           * Transform.Translation(xOffset, ANCHOR_Y, 0);
                var contnr = Transform.Translation(0, 0, 0)
                           * Transform.RotationX(m_pitch)
                           * Transform.Translation(0, 0, CONTNR_Z);

                return contnr * anchor * origin;
            }

            var worldNormal = GetAtRoll((TargetBaseRoll + TargetEffectRoll) * 360 + roll, TargetOffset + TargetEffectOffset);
            var worldNoRoll = GetAtRoll(0, 0);
            // TODO(local): does this need to use offset?
            var worldCritLine = GetAtRoll(TargetBaseRoll * 360 + roll, TargetOffset + TargetEffectOffset);

            Vector3 ZoomDirection(Transform t, out float dist)
            {
                var dir = ((Matrix4x4)t).Translation;
                dist = dir.Length();
                return Vector3.Normalize(dir);
            }

            var zoomDir = ZoomDirection(worldNormal, out float highwayDist);
            var zoomTransform = Transform.Translation(zoomDir * m_zoom * highwayDist);

            DefaultTransform = worldNoRoll;
            DefaultZoomedTransform = worldNoRoll * Transform.Translation(ZoomDirection(worldNoRoll, out float zoomedDist) * m_zoom * zoomedDist);
            WorldTransform = worldNormal * zoomTransform;
            CritLineTransform = worldCritLine;

            var critDir = Vector3.Normalize(((Matrix4x4)worldNoRoll).Translation);
            float rotToCrit = MathL.Atan(critDir.Y, -critDir.Z);
            
            float cameraRot = Camera.FieldOfView / 2 - Camera.FieldOfView * CritScreenY;
            float cameraPitch = rotToCrit + MathL.ToRadians(cameraRot);

            Camera.Position = CameraOffset;
            Camera.Rotation = Quaternion.CreateFromYawPitchRoll(0, cameraPitch, 0);

            HorizonHeight = Camera.Project(WorldTransform, Camera.Position + new Vector3(0, 0, -1)).Y;

            Vector3 V3Project(Vector3 a, Vector3 b) => b * (Vector3.Dot(a, b) / Vector3.Dot(b, b));

            float SignedDistance(Vector3 point, Vector3 ray)
            {
                Vector3 projected = V3Project(point, ray);
                return MathL.Sign(Vector3.Dot(ray, projected)) * projected.Length();
            }

            float minClipDist = float.MaxValue;
            float maxClipDist = float.MinValue;

            Vector3 cameraForward = Vector3.Transform(new Vector3(0, 0, -1), Camera.Rotation);
            for (int i = 0; i < 4; i++)
            {
                float clipDist = SignedDistance(Vector3.Transform(m_clipPoints[i], WorldTransform.Matrix) - Camera.Position, cameraForward);

                minClipDist = Math.Min(minClipDist, clipDist);
                maxClipDist = Math.Max(maxClipDist, clipDist);
            }

            float clipNear = Math.Max(0.01f, minClipDist);
            float clipFar = maxClipDist;

            // TODO(local): see if the default epsilon is enough? There's no easy way to check clip planes manually right now
            if (clipNear.ApproxEq(clipFar))
                clipFar = clipNear + 0.001f;

            Camera.NearDistance = clipNear;
            Camera.FarDistance = clipFar;
        }

        private Vector3[] m_clipPoints = new Vector3[4] { new Vector3(-1, 0, LENGTH_ADD), new Vector3(1, 0, LENGTH_ADD), new Vector3(-1, 0, -LENGTH_BASE), new Vector3(1, 0, -LENGTH_BASE) };

        public void Render()
        {
            var renderState = new RenderState
            {
                ProjectionMatrix = Camera.ProjectionMatrix,
                CameraMatrix = Camera.ViewMatrix,
            };

            using (var queue = new RenderQueue(renderState))
            {
                m_highwayDrawable.DrawToQueue(queue, Transform.Translation(0, 0, LENGTH_ADD) * WorldTransform);

                for (int i = 0; i < 6; i++)
                {
                    var keyBeamInfo = m_keyBeamInfos[i];
                    var keyBeamDrawable = m_keyBeamDrawables[i];

                    Transform t = Transform.Scale(i < 4 ? 1.0f / 6 : 2.0f / 6, 1, 1)
                                * Transform.Translation(i < 4 ? -3.0f / 12 + (float)i / 6 : -1.0f / 6 + (2.0f * (i - 4)) / 6, 0, LENGTH_ADD)
                                * WorldTransform;

                    keyBeamDrawable.Params["Color"] = new Vector4(keyBeamInfo.Color, keyBeamInfo.Alpha);
                    keyBeamDrawable.DrawToQueue(queue, t);
                }

                void RenderButtonStream(int i)
                {
                    foreach (var objr in m_renderables[i].Values)
                    {
                        float zAbs = (float)((objr.Object.AbsolutePosition - PlaybackPosition) / ViewDuration);
                        float z = LENGTH_BASE * zAbs;

                        float xOffs = 0;
                        if (i < 4)
                            xOffs = -3 / 12.0f + i / 6.0f;
                        else xOffs = -1 / 6.0f + (i - 4) / 3.0f;

                        // TODO(local): [CONFIG] Allow user to change the scaling of chips, or use a different texture
                        Transform tDiff = Transform.Identity;
                        if (objr.Object.IsInstant)
                        {
                            float distScaling = zAbs * 1.0f;
                            float widthMult = 1.0f;

                            if (objr.Object.Stream < 4)
                            {
                                int fxLaneCheck = 4 + objr.Object.Stream / 2;
                                if (objr.Object.Chart[fxLaneCheck].TryGetAt(objr.Object.Position, out var overlap) && overlap.IsInstant)
                                    widthMult = 0.8f;
                            }

                            tDiff = Transform.Scale(widthMult, 1, 1 + distScaling);
                        }

                        if (objr is ButtonHoldRenderState3D holdObj)
                        {
                            if (m_glowInfos[objr.Object.Stream].Object == objr.Object)
                            {
                                holdObj.Glow = m_glowInfos[objr.Object.Stream].Glow;
                                holdObj.GlowState = m_glowInfos[objr.Object.Stream].GlowState;
                            }
                            else
                            {
                                holdObj.Glow = m_streamsActive[objr.Object.Stream] ? 0.0f : -0.5f;
                                holdObj.GlowState = m_streamsActive[objr.Object.Stream] ? 1 : 0;
                            }
                        }

                        Transform t = tDiff * Transform.Translation(xOffs, 0, -z) * WorldTransform;
                        objr.Render(queue, t);
                    }
                }

                void RenderAnalogStream(int i)
                {
                    const float HISCALE = 0.1f;

                    foreach (var objr in m_renderables[i + 6].Values)
                    {
                        var analog = objr.Object as AnalogObject;

                        time_t position = objr.Object.AbsolutePosition;
                        if (objr.Object.PreviousConnected != null && objr.Object.Previous.IsInstant)
                            position += SlamDurationTime(objr.Object.PreviousConnected);

                        float z = LENGTH_BASE * (float)((position - PlaybackPosition) / ViewDuration);

                        Transform s = Transform.Scale(1, 1, 1 + HISCALE);
                        Transform t = Transform.Translation(0, 0, -z) * Transform.Scale(1, 1, 1 + HISCALE) * WorldTransform;
                        objr.Render(queue, t);

                        if (objr.Object.PreviousConnected == null)
                        {
                            float laneSpace = 5 / 6.0f;
                            if (analog.RangeExtended) laneSpace *= 2;

                            time_t entryPosition = objr.Object.AbsolutePosition;
                            float zEntry = LENGTH_BASE * (float)((entryPosition - PlaybackPosition) / ViewDuration);

                            Transform tEntry = Transform.Translation(((objr.Object as AnalogObject).InitialValue - 0.5f) * laneSpace, 0, -zEntry) * Transform.Scale(1, 1, 1 + HISCALE) * WorldTransform;
                            //queue.Draw(tEntry, laserEntryMesh, laserEntryMaterial, i == 0 ? lLaserEntryParams : rLaserEntryParams);
                            (i == 0 ? m_lVolEntryDrawable : m_rVolEntryDrawable).DrawToQueue(queue, tEntry);
                        }

                        if (objr.Object.NextConnected == null && objr.Object.IsInstant)
                        {
                            float laneSpace = 5 / 6.0f;
                            if (analog.RangeExtended) laneSpace *= 2;

                            time_t exitPosition = objr.Object.AbsoluteEndPosition;
                            if (objr.Object.IsInstant)
                                exitPosition += SlamDurationTime(objr.Object);

                            float zExit = LENGTH_BASE * (float)((exitPosition - PlaybackPosition) / ViewDuration);

                            Transform tExit = Transform.Translation(((objr.Object as AnalogObject).FinalValue - 0.5f) * laneSpace, 0, -zExit) * Transform.Scale(1, 1, 1 + HISCALE) * WorldTransform;
                            //queue.Draw(tExit, laserExitMesh, laserExitMaterial, i == 0 ? lLaserExitParams : rLaserExitParams);
                            (i == 0 ? m_lVolExitDrawable : m_rVolExitDrawable).DrawToQueue(queue, tExit);
                        }
                    }
                }

                for (int i = 0; i < 2; i++)
                    RenderButtonStream(i + 4);

                for (int i = 0; i < 4; i++)
                    RenderButtonStream(i);

                for (int i = 0; i < 2; i++)
                    RenderAnalogStream(i);
            }
        }
    }
}