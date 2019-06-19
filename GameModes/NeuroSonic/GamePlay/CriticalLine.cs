﻿using System.Numerics;

using OpenGL;

using theori.Gui;
using theori.Graphics;
using System;
using theori.Resources;

namespace NeuroSonic.GamePlay
{
    public sealed class CriticalLine : Panel
    {
        private bool m_isDirty = true;

        private readonly Panel m_container;
        private readonly Sprite m_image, m_capLeft, m_capRight, m_cursorLeft, m_cursorRight;

        private float m_horHeight, m_critHeight;
        private float m_laserRoll, m_baseRoll, m_addRoll, m_addOffset;
        private float m_leftPos, m_rightPos;

        public float HorizonHeight { get => m_horHeight; set { m_horHeight = value; m_isDirty = true; } }
        public float CriticalHeight { get => m_critHeight; set { m_critHeight = value; m_isDirty = true; } }
        
        public float LaserRoll { get => m_laserRoll; set { m_laserRoll = value; m_isDirty = true; } }
        public float BaseRoll { get => m_baseRoll; set { m_baseRoll = value; m_isDirty = true; } }
        public float EffectRoll { get => m_addRoll; set { m_addRoll = value; m_isDirty = true; } }
        public float EffectOffset { get => m_addOffset; set { m_addOffset = value; m_isDirty = true; } }

        public float LeftCursorPosition { get => m_leftPos; set { m_leftPos = value; m_isDirty = true; } }
        public float RightCursorPosition { get => m_rightPos; set { m_rightPos = value; m_isDirty = true; } }

        public CriticalLine(ClientResourceManager skin)
        {
            var lVolColor = Color.HSVtoRGB(new Vector3(Plugin.Config.GetInt(NscConfigKey.Laser0Color) / 360.0f, 1, 1));
            var rVolColor = Color.HSVtoRGB(new Vector3(Plugin.Config.GetInt(NscConfigKey.Laser1Color) / 360.0f, 1, 1));

            var critTexture = skin.AquireTexture("textures/scorebar");
            var capTexture = skin.AquireTexture("textures/critical_cap");
            var cursorTexture = skin.AquireTexture("textures/cursor");

            RelativeSizeAxes = Axes.X;
            Size = new Vector2(0.75f, 0);

            Children = new GuiElement[]
            {
                m_container = new Panel()
                {
                    RelativeSizeAxes = Axes.X,
                    Size = new Vector2(1, 0),

                    Children = new GuiElement[]
                    {
                        m_image = new Sprite(critTexture),
                        m_capLeft = new Sprite(capTexture)
                        {
                            Position = new Vector2(20, 0),
                        },
                        m_capRight = new Sprite(capTexture)
                        {
                            Scale = new Vector2(-1, 1),
                        },
                    }
                },

                m_cursorLeft = new Sprite(cursorTexture) { Color = new Vector4(lVolColor, 1) },
                m_cursorRight = new Sprite(cursorTexture) { Color = new Vector4(rVolColor, 1) },
            };

            float critImageWidth = m_image.Size.X;

            m_container.Size = new Vector2(critImageWidth, 0);
            m_container.Origin = m_container.Size / 2;
            
            m_image.Origin = new Vector2(0, m_image.Size.Y / 2);

            m_capLeft.Origin = new Vector2(m_capLeft.Size.X, m_capLeft.Size.Y / 2);

            m_capRight.Position = new Vector2(critImageWidth - 20, 0);
            m_capRight.Origin = new Vector2(m_capRight.Size.X, m_capRight.Size.Y / 2);

            m_cursorLeft.Origin = m_cursorLeft.Size / 2;
            m_cursorLeft.Position = new Vector2(critImageWidth / 2 - 100, 0);

            m_cursorRight.Origin = m_cursorRight.Size / 2;
            m_cursorRight.Position = new Vector2(critImageWidth / 2 + 100, 0);
        }

        public override void Update()
        {
            if (m_isDirty)
            {
                UpdateOrientation();
                m_isDirty = false;
            }
        }

        private void UpdateOrientation()
        {
            float desiredCritWidth = Window.Width * 0.75f;

            m_container.Scale = new Vector2(desiredCritWidth / m_image.Size.X);

            m_cursorLeft.Position = new Vector2(LeftCursorPosition, 0);
            m_cursorRight.Position = new Vector2(RightCursorPosition, 0);
        }
    }
}