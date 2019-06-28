﻿using System;
using System.Numerics;

using NeuroSonic.GamePlay.Scoring;

using OpenRM;
using OpenRM.Audio.Effects;
using OpenRM.Voltex;

using theori;
using theori.Audio;
using theori.Graphics;
using theori.Gui;
using theori.IO;
using theori.Resources;

namespace NeuroSonic.GamePlay
{
    [Flags]
    public enum AutoPlay
    {
        None = 0,

        Buttons = 0x01,
        Lasers = 0x02,

        ButtonsAndLasers = Buttons | Lasers,
    }

    public sealed class GameLayer : NscLayer
    {
        public override int TargetFrameRate => 288;

        public override bool BlocksParentLayer => true;

        private readonly AutoPlay m_autoPlay;

        private bool AutoButtons => (m_autoPlay & AutoPlay.Buttons) != 0;
        private bool AutoLasers => (m_autoPlay & AutoPlay.Lasers) != 0;

        private readonly ClientResourceLocator m_locator;
        private readonly ClientResourceManager m_resources;

        private HighwayControl m_highwayControl;
        private HighwayView m_highwayView;

        private CriticalLine m_critRoot;
        private ComboDisplay m_comboDisplay;

        private Chart m_chart;
        private SlidingChartPlayback m_playback;
        private MasterJudge m_judge;

        private AudioEffectController m_audioController;
        private AudioTrack m_audio;
        private AudioSample m_slamSample;

        private readonly OpenRM.Object[] m_activeObjects = new OpenRM.Object[8];
        private readonly bool[] m_streamHasActiveEffects = new bool[8].Fill(true);

        private readonly EffectDef[] m_currentEffects = new EffectDef[8];

        private time_t CurrentQuarterNodeDuration => m_chart.ControlPoints.MostRecent(m_audioController.Position).QuarterNoteDuration;

        #region Debug Overlay

        private GameDebugOverlay m_debugOverlay;

        #endregion

        internal GameLayer(ClientResourceLocator resourceLocator, Chart chart, AudioTrack audio, AutoPlay autoPlay = AutoPlay.None)
        {
            m_locator = resourceLocator;
            m_resources = new ClientResourceManager(resourceLocator);

            m_chart = chart;
            m_audio = audio;

            m_autoPlay = autoPlay;

            m_highwayView = new HighwayView(m_resources);
        }

        public override void Destroy()
        {
            base.Destroy();

            m_resources.Dispose();

            if (m_debugOverlay != null)
            {
                Host.RemoveOverlay(m_debugOverlay);
                m_debugOverlay = null;
            }

            m_audioController?.Stop();
            m_audioController?.Dispose();
        }

        public override bool AsyncLoad()
        {
            if (!m_highwayView.AsyncLoad())
                return false;

            m_slamSample = m_resources.QueueAudioSampleLoad("audio/slam");

            if (!m_resources.LoadAll())
                return false;

            return true;
        }

        public override bool AsyncFinalize()
        {
            if (!m_highwayView.AsyncFinalize())
                return false;

            if (!m_resources.FinalizeLoad())
                return false;

            m_slamSample.Channel = Host.Mixer.MasterChannel;

            return true;
        }

        public override void ClientSizeChanged(int width, int height)
        {
            m_highwayView.Camera.AspectRatio = Window.Aspect;
        }

        public override void Init()
        {
            base.Init();

            m_highwayControl = new HighwayControl(HighwayControlConfig.CreateDefaultKsh168());

            m_playback = new SlidingChartPlayback(m_chart);
            m_playback.ObjectHeadCrossPrimary += (dir, obj) =>
            {
                if (dir == PlayDirection.Forward)
                    m_highwayView.RenderableObjectAppear(obj);
                else m_highwayView.RenderableObjectDisappear(obj);
            };
            m_playback.ObjectTailCrossSecondary += (dir, obj) =>
            {
                if (dir == PlayDirection.Forward)
                    m_highwayView.RenderableObjectDisappear(obj);
                else m_highwayView.RenderableObjectAppear(obj);
            };

            // TODO(local): Effects wont work with backwards motion, but eventually the
            //  editor (with the only backwards motion support) will pre-render audio instead.
            m_playback.ObjectHeadCrossCritical += (dir, obj) =>
            {
                if (dir != PlayDirection.Forward) return;

                if (obj is Event evt)
                    PlaybackEventTrigger(evt, dir);
                else PlaybackObjectBegin(obj);
            };
            m_playback.ObjectTailCrossCritical += (dir, obj) =>
            {
                if (dir == PlayDirection.Backward && obj is Event evt)
                    PlaybackEventTrigger(evt, dir);
                else PlaybackObjectEnd(obj);
            };

            m_highwayView.ViewDuration = m_playback.LookAhead;

            ForegroundGui = new Panel()
            {
                Children = new GuiElement[]
                {
                    m_critRoot = new CriticalLine(m_resources),
                    m_comboDisplay = new ComboDisplay(m_resources)
                    {
                        RelativePositionAxes = Axes.Both,
                        Position = new Vector2(0.5f, 0.7f)
                    },
                }
            };

            m_judge = new MasterJudge(m_chart);
            for (int i = 0; i < 6; i++)
            {
                int stream = i;

                var judge = (ButtonJudge)m_judge[i];
                //judge.JudgementOffset = 0.032;
                judge.JudgementOffset = Plugin.Config.GetInt(NscConfigKey.InputOffset) / 1000.0f;
                judge.AutoPlay = AutoButtons;
                judge.OnChipPressed += Judge_OnChipPressed;
                judge.OnTickProcessed += Judge_OnTickProcessed;
                judge.OnHoldPressed += Judge_OnHoldPressed;
                judge.OnHoldReleased += Judge_OnHoldReleased;
            }

            m_highwayControl = new HighwayControl(HighwayControlConfig.CreateDefaultKsh168());
            m_highwayView.Reset();

            m_audio.Volume = 0.8f;
            m_audio.Position = m_chart.Offset;
            m_audioController = new AudioEffectController(8, m_audio, true)
            {
                RemoveFromChannelOnFinish = true,
            };
            m_audioController.Finish += () =>
            {
                Logger.Log("track complete");
                Host.PopToParent(this);
            };

            time_t firstObjectTime = double.MaxValue;
            for (int s = 0; s < m_chart.StreamCount; s++)
                firstObjectTime = MathL.Min((double)firstObjectTime, m_chart.ObjectStreams[s].FirstObject?.AbsolutePosition.Seconds ?? double.MaxValue);

            m_audioController.Position = MathL.Min(0.0, (double)firstObjectTime - 2);
            m_audioController.Play();
        }

        public override void Suspended()
        {
            if (m_debugOverlay != null)
            {
                Host.RemoveOverlay(m_debugOverlay);
                m_debugOverlay = null;
            }
            m_audioController?.Stop();
        }

        public override void Resumed()
        {
            m_audioController?.Play();
        }

        private void PlaybackObjectBegin(OpenRM.Object obj)
        {
            if (obj is AnalogObject aobj)
            {
                if (obj.IsInstant)
                {
                    int dir = -MathL.Sign(aobj.FinalValue - aobj.InitialValue);
                    m_highwayControl.ShakeCamera(dir);

                    if (aobj.InitialValue == (aobj.Stream == 6 ? 0 : 1) && aobj.NextConnected == null)
                        m_highwayControl.ApplyRollImpulse(-dir);
                    m_slamSample.Play();
                }

                if (aobj.PreviousConnected == null)
                {
                    if (!AreLasersActive) m_audioController.SetEffect(6, CurrentQuarterNodeDuration, currentLaserEffectDef, BASE_LASER_MIX);
                    currentActiveLasers[obj.Stream - 6] = true;
                }

                m_activeObjects[obj.Stream] = aobj.Head;
            }
            else if (obj is ButtonObject bobj)
            {
                //if (bobj.HasEffect) m_audioController.SetEffect(obj.Stream, CurrentQuarterNodeDuration, bobj.Effect);
                //else m_audioController.RemoveEffect(obj.Stream);

                // NOTE(local): can move this out for analog as well, but it doesn't matter RN
                if (!bobj.IsInstant)
                    m_activeObjects[obj.Stream] = obj;
            }
        }

        private void PlaybackObjectEnd(OpenRM.Object obj)
        {
            if (obj is AnalogObject aobj)
            {
                if (aobj.NextConnected == null)
                {
                    currentActiveLasers[obj.Stream - 6] = false;
                    if (!AreLasersActive) m_audioController.RemoveEffect(6);

                    if (m_activeObjects[obj.Stream] == aobj.Head)
                        m_activeObjects[obj.Stream] = null;
                }
            }
            if (obj is ButtonObject bobj)
            {
                //m_audioController.RemoveEffect(obj.Stream);

                // guard in case the Begin function already overwrote us
                if (m_activeObjects[obj.Stream] == obj)
                    m_activeObjects[obj.Stream] = null;
            }
        }

        private void Judge_OnTickProcessed(OpenRM.Object obj, time_t position, JudgeResult result)
        {
            //Logger.Log($"[{ obj.Stream }] { result.Kind } :: { (int)(result.Difference * 1000) } @ { position }");

            if (result.Kind == JudgeKind.Miss || result.Kind == JudgeKind.Bad)
                m_comboDisplay.Combo = 0;
            else m_comboDisplay.Combo++;

            if (!obj.IsInstant)
                m_streamHasActiveEffects[obj.Stream] = result.Kind != JudgeKind.Miss;
            else
            {
                if (result.Kind != JudgeKind.Miss)
                    CreateKeyBeam(obj.Stream, result.Kind, result.Difference < 0.0);
            }
        }

        private void Judge_OnChipPressed(time_t position, OpenRM.Object obj)
        {
        }

        private void Judge_OnHoldReleased(time_t position, OpenRM.Object obj)
        {
            m_streamHasActiveEffects[obj.Stream] = false;
        }

        private void Judge_OnHoldPressed(time_t position, OpenRM.Object obj)
        {
            m_streamHasActiveEffects[obj.Stream] = true;
            CreateKeyBeam(obj.Stream, JudgeKind.Passive, false);
        }

        private void PlaybackEventTrigger(Event evt, PlayDirection direction)
        {
            if (direction == PlayDirection.Forward)
            {
                switch (evt)
                {
                    case EffectKindEvent effectKind:
                    {
                        var effect = m_currentEffects[effectKind.EffectIndex] = effectKind.Effect;
                        if (effect == null)
                            m_audioController.RemoveEffect(effectKind.EffectIndex);
                        else m_audioController.SetEffect(effectKind.EffectIndex, CurrentQuarterNodeDuration, effect, 1.0f);
                    }
                    break;

                    case LaserApplicationEvent app: m_highwayControl.LaserApplication = app.Application; break;

                    // TODO(local): left/right lasers separate + allow both independent if needed
                    case LaserFilterGainEvent filterGain: laserGain = filterGain.Gain; break;
                    case LaserFilterKindEvent filterKind:
                    {
                        m_audioController.SetEffect(6, CurrentQuarterNodeDuration, currentLaserEffectDef = filterKind.FilterEffect, m_audioController.GetEffectMix(6));
                    }
                    break;

                    case LaserParamsEvent pars:
                    {
                        if (pars.LaserIndex.HasFlag(LaserIndex.Left)) m_highwayControl.LeftLaserParams = pars.Params;
                        if (pars.LaserIndex.HasFlag(LaserIndex.Right)) m_highwayControl.RightLaserParams = pars.Params;
                    }
                    break;

                    case SlamVolumeEvent pars: m_slamSample.Volume = pars.Volume; break;
                }
            }

            switch (evt)
            {
                case SpinImpulseEvent spin: m_highwayControl.ApplySpin(spin.Params, spin.AbsolutePosition); break;
                case SwingImpulseEvent swing: m_highwayControl.ApplySwing(swing.Params, swing.AbsolutePosition); break;
                case WobbleImpulseEvent wobble: m_highwayControl.ApplyWobble(wobble.Params, wobble.AbsolutePosition); break;
            }
        }

        protected internal override bool ControllerButtonPressed(ControllerInput input)
        {
            switch (input)
            {
                case ControllerInput.BT0: UserInput_BtPress(0); break;
                case ControllerInput.BT1: UserInput_BtPress(1); break;
                case ControllerInput.BT2: UserInput_BtPress(2); break;
                case ControllerInput.BT3: UserInput_BtPress(3); break;
                case ControllerInput.FX0: UserInput_BtPress(4); break;
                case ControllerInput.FX1: UserInput_BtPress(5); break;

                default: return false;
            }

            return true;
        }

        protected internal override bool ControllerButtonReleased(ControllerInput input)
        {
            switch (input)
            {
                case ControllerInput.BT0: UserInput_BtRelease(0); break;
                case ControllerInput.BT1: UserInput_BtRelease(1); break;
                case ControllerInput.BT2: UserInput_BtRelease(2); break;
                case ControllerInput.BT3: UserInput_BtRelease(3); break;
                case ControllerInput.FX0: UserInput_BtRelease(4); break;
                case ControllerInput.FX1: UserInput_BtRelease(5); break;

                default: return false;
            }

            return true;
        }

        protected internal override bool ControllerAxisChanged(ControllerInput input, float delta)
        {
            switch (input)
            {
                default: return false;
            }

            return true;
        }

        public override bool KeyPressed(KeyInfo key)
        {
            if ((key.Mods & KeyMod.ALT) != 0 && key.KeyCode == KeyCode.D)
            {
                if (m_debugOverlay != null)
                {
                    Host.RemoveOverlay(m_debugOverlay);
                    m_debugOverlay = null;
                }
                else
                {
                    m_debugOverlay = new GameDebugOverlay(m_resources);
                    Host.AddOverlay(m_debugOverlay);
                }
                return true;
            }

            switch (key.KeyCode)
            {
                case KeyCode.ESCAPE:
                {
                    Host.PopToParent(this);
                } break;

                // TODO(local): consume whatever the controller does
                default: return false;
            }

            return true;
        }

        void UserInput_BtPress(int streamIndex)
        {
            if (AutoButtons) return;

            var result = (m_judge[streamIndex] as ButtonJudge).UserPressed(m_judge.Position);
            if (result == null)
                m_highwayView.CreateKeyBeam(streamIndex, Vector3.One);
            else m_debugOverlay?.AddTimingInfo(result.Value.Difference, result.Value.Kind);
            //else CreateKeyBeam(streamIndex, result.Value.Kind, result.Value.Difference < 0.0);
        }

        void UserInput_BtRelease(int streamIndex)
        {
            if (AutoButtons) return;

            (m_judge[streamIndex] as ButtonJudge).UserReleased(m_judge.Position);
        }

        private void CreateKeyBeam(int streamIndex, JudgeKind kind, bool isEarly)
        {
            Vector3 color = Vector3.One;

            switch (kind)
            {
                case JudgeKind.Passive:
                case JudgeKind.Perfect: color = new Vector3(1, 1, 0); break;
                case JudgeKind.Critical: color = new Vector3(1, 1, 0); break;
                case JudgeKind.Near: color = isEarly ? new Vector3(1.0f, 0, 1.0f) : new Vector3(0.5f, 1, 0.25f); break;
                case JudgeKind.Bad:
                case JudgeKind.Miss: color = new Vector3(1, 0, 0); break;
            }

            m_highwayView.CreateKeyBeam(streamIndex, color);
        }

        public override void Update(float delta, float total)
        {
            base.Update(delta, total);

            time_t position = m_audio?.Position ?? 0;

            m_judge.Position = position;
            m_highwayControl.Position = position;
            m_playback.Position = position;

            float GetPathValueLerped(int stream)
            {
                var s = m_playback.Chart[stream];

                var mrPoint = s.MostRecent<PathPointEvent>(position);
                if (mrPoint == null)
                    return ((PathPointEvent)s.FirstObject)?.Value ?? 0;

                if (mrPoint.HasNext)
                {
                    float alpha = (float)((position - mrPoint.AbsolutePosition).Seconds / (mrPoint.Next.AbsolutePosition - mrPoint.AbsolutePosition).Seconds);
                    return MathL.Lerp(mrPoint.Value, ((PathPointEvent)mrPoint.Next).Value, alpha);
                }
                else return mrPoint.Value;
            }

            m_highwayControl.MeasureDuration = m_chart.ControlPoints.MostRecent(position).MeasureDuration;

            float leftLaserPos = GetTempRollValue(position, 6, out float leftLaserRange);
            float rightLaserPos = GetTempRollValue(position, 7, out float rightLaserRange, true);

            m_highwayControl.LeftLaserInput = leftLaserPos;
            m_highwayControl.RightLaserInput = rightLaserPos;

            m_highwayControl.Zoom = GetPathValueLerped(StreamIndex.Zoom);
            m_highwayControl.Pitch = GetPathValueLerped(StreamIndex.Pitch);
            m_highwayControl.Offset = GetPathValueLerped(StreamIndex.Offset);
            m_highwayControl.Roll = GetPathValueLerped(StreamIndex.Roll);

            m_highwayView.PlaybackPosition = position;

            for (int i = 0; i < 8; i++)
            {
                bool active = m_streamHasActiveEffects[i] && m_activeObjects[i] != null;
                if (i == 6)
                    active |= m_streamHasActiveEffects[i + 1] && m_activeObjects[i + 1] != null;
                m_audioController.SetEffectActive(i, active);
            }

            UpdateEffects();
            m_audioController.EffectsActive = true;

            m_highwayControl.Update(Time.Delta);
            m_highwayControl.ApplyToView(m_highwayView);

            for (int i = 0; i < 8; i++)
            {
                var obj = m_activeObjects[i];

                m_highwayView.SetStreamActive(i, m_streamHasActiveEffects[i]);
                m_debugOverlay?.SetStreamActive(i, m_streamHasActiveEffects[i]);

                if (obj == null) continue;

                float glow = -0.5f;
                int glowState = 0;

                if (m_streamHasActiveEffects[i])
                {
                    glow = MathL.Cos(10 * MathL.TwoPi * (float)position) * 0.35f;
                    glowState = 2 + MathL.FloorToInt(position.Seconds * 20) % 2;
                }

                m_highwayView.SetObjectGlow(obj, glow, glowState);
            }
            m_highwayView.Update();

            {
                var camera = m_highwayView.Camera;

                var defaultTransform = m_highwayView.DefaultTransform;
                var defaultZoomTransform = m_highwayView.DefaultZoomedTransform;
                var totalWorldTransform = m_highwayView.WorldTransform;
                var critLineTransform = m_highwayView.CritLineTransform;

                Vector2 comboLeft = camera.Project(defaultTransform, new Vector3(-0.8f / 6, 0, 0));
                Vector2 comboRight = camera.Project(defaultTransform, new Vector3(0.8f / 6, 0, 0));

                m_comboDisplay.DigitSize = (comboRight.X - comboLeft.X) / 4;

                Vector2 critRootPosition = camera.Project(critLineTransform, Vector3.Zero);
                Vector2 critRootPositionWest = camera.Project(critLineTransform, new Vector3(-1, 0, 0));
                Vector2 critRootPositionEast = camera.Project(critLineTransform, new Vector3(1, 0, 0));
                Vector2 critRootPositionForward = camera.Project(critLineTransform, new Vector3(0, 0, -1));

                float GetCursorPosition(float xWorld)
                {
                    var critRootCenter = camera.Project(defaultZoomTransform, Vector3.Zero);
                    var critRootCursor = camera.Project(defaultZoomTransform, new Vector3(xWorld, 0, 0));
                    return critRootCursor.X - critRootCenter.X;
                }

                m_critRoot.LeftCursorPosition = GetCursorPosition((leftLaserPos - 0.5f) * 5.0f / 6 * leftLaserRange);
                m_critRoot.RightCursorPosition = GetCursorPosition(-(rightLaserPos - 0.5f) * 5.0f / 6 * rightLaserRange);

                Vector2 critRotationVector = critRootPositionEast - critRootPositionWest;
                float critRootRotation = MathL.Atan(critRotationVector.Y, critRotationVector.X);

                m_critRoot.LaserRoll = m_highwayView.LaserRoll;
                m_critRoot.BaseRoll = m_highwayControl.Roll * 360;
                m_critRoot.EffectRoll = m_highwayControl.EffectRoll;
                m_critRoot.EffectOffset = m_highwayControl.EffectOffset;
                m_critRoot.Position = critRootPosition;
                m_critRoot.Rotation = MathL.ToDegrees(critRootRotation) + m_highwayControl.CritLineEffectRoll * 25;
            }
        }

        private void UpdateEffects()
        {
            UpdateLaserEffects();
        }

        private EffectDef currentLaserEffectDef = EffectDef.GetDefault(EffectType.PeakingFilter);
        private readonly bool[] currentActiveLasers = new bool[2];
        private readonly float[] currentActiveLaserAlphas = new float[2];

        private bool AreLasersActive => currentActiveLasers[0] || currentActiveLasers[1];

        private const float BASE_LASER_MIX = 0.8f;
        private float laserGain = 0.5f;

        private float GetTempRollValue(time_t position, int stream, out float valueMult, bool oneMinus = false)
        {
            var s = m_playback.Chart[stream];
            valueMult = 1.0f;

            var mrAnalog = s.MostRecent<AnalogObject>(position);
            if (mrAnalog == null || position > mrAnalog.AbsoluteEndPosition)
                return 0;

            if (mrAnalog.RangeExtended)
                valueMult = 2.0f;
            float result = mrAnalog.SampleValue(position);
            if (oneMinus)
                return 1 - result;
            else return result;
        }

        private void UpdateLaserEffects()
        {
            if (!AreLasersActive)
            {
                m_audioController.SetEffectMix(6, 0);
                return;
            }

            float LaserAlpha(int index)
            {
                return GetTempRollValue(m_audio.Position, index + 6, out float _, index == 1);
            }

            if (currentActiveLasers[0])
                currentActiveLaserAlphas[0] = LaserAlpha(0);
            if (currentActiveLasers[1])
                currentActiveLaserAlphas[1] = LaserAlpha(1);

            float alpha;
            if (currentActiveLasers[0] && currentActiveLasers[1])
                alpha = Math.Max(currentActiveLaserAlphas[0], currentActiveLaserAlphas[1]);
            else if (currentActiveLasers[0])
                alpha = currentActiveLaserAlphas[0];
            else alpha = currentActiveLaserAlphas[1];

            m_audioController.UpdateEffect(6, CurrentQuarterNodeDuration, alpha);

            float mix = 1.0f;
            if (currentLaserEffectDef != null)
            {
                if (currentLaserEffectDef.Type == EffectType.PeakingFilter)
                {
                    mix = BASE_LASER_MIX * laserGain;
                    if (alpha < 0.1f)
                        mix *= alpha / 0.1f;
                    else if (alpha > 0.8f)
                        mix *= 1 - (alpha - 0.8f) / 0.2f;
                }
                else
                {
                    switch (currentLaserEffectDef.Type)
                    {
                        case EffectType.HighPassFilter:
                        case EffectType.LowPassFilter:
                            mix = laserGain;
                            break;
                    }
                }
            }

            m_audioController.SetEffectMix(6, mix);
        }

        public override void Render()
        {
            m_highwayView.Render();
        }
    }
}
