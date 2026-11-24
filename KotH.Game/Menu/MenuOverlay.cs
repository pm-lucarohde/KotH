using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Embedding;
using Avalonia.Controls.Embedding.Offscreen;
using Avalonia.Controls.Platform.Surfaces;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using KotH.Client.Hud;
using Stride.Core.Mathematics;
using Stride.Graphics;
using AvaloniaPixelFormat = Avalonia.Platform.PixelFormat;
using Point = Avalonia.Point;
using Texture = Stride.Graphics.Texture;
using Color = Stride.Core.Mathematics.Color;

namespace KotH.Client.Menu
{
    /// <summary>One part of an Avalonia page over the screen: an offscreen top level as big as the
    /// part; Skia redraws only what changed into one kept buffer, the changed rows are uploaded once
    /// per new frame and the HUD draws it (headless windows took a fresh bitmap per frame). Placed in
    /// units of a screen 1080 high from a corner or the top middle.</summary>
    public sealed class MenuOverlay : IHudElement, IDisposable
    {
        /// <summary>The screen corner a part is placed from; the centres centre it, x moves it right.</summary>
        public enum Corner { TopLeft, TopRight, BottomRight, TopCenter, BottomCenter, BottomLeft }

        /// <summary>The height parts are laid out for, in their units.</summary>
        public const double DesignHeight = 1080;

        static bool started;
        static long tickedFrame = -1;

        readonly Surface surface = new Surface();
        readonly EmbeddableControlRoot root;
        readonly LayoutTransformControl scaler;
        readonly Corner corner;
        readonly Vector2 offset, size;
        readonly float zoom;
        RectangleF rect;
        Vector2 placedFor;
        Texture texture;

        // The rows changed and not yet uploaded.
        readonly bool[][] pending = new bool[1][];

        /// <summary>Whether it is drawn and takes the pointer.</summary>
        public bool Visible { get; set; } = true;

        /// <summary>Drawn this far off its place, in pixels (the entrance; the pointer ignores it).</summary>
        public Vector2 Shift { get; set; }

        /// <summary>Drawn this much bigger round its centre.</summary>
        public float Zoom { get; set; } = 1f;

        /// <summary>Drawn this opaque.</summary>
        public float Alpha { get; set; } = 1f;

        /// <summary>Drawn turned this far round its centre, in radians, clockwise.</summary>
        public float Angle { get; set; }

        /// <summary>Only this share of it is drawn, from the left (a filling bar).</summary>
        public float Reveal { get; set; } = 1f;

        /// <param name="part">The part, sized in units of a screen 1080 high.</param>
        /// <param name="corner">The screen corner it is placed from.</param>
        /// <param name="x">Its distance from that corner sideways (y: up or down), in the same units.</param>
        /// <param name="zoom">Part and distances this much bigger, such as the whole HUD.</param>
        public MenuOverlay(Control part, Corner corner, float x, float y, float zoom = 1f)
        {
            this.corner = corner;
            this.zoom = zoom;
            offset = new Vector2(x, y);
            size = new Vector2((float)part.Width, (float)part.Height);
            scaler = new LayoutTransformControl { Child = part };
            // An offscreen top level reports no transparency, so the fallback is what shows.
            root = new EmbeddableControlRoot(surface)
            {
                Content = scaler,
                Background = Brushes.Transparent,
                TransparencyBackgroundFallback = Brushes.Transparent,
            };
            // Grey antialiasing: coloured subpixel fringes assume an opaque background.
            RenderOptions.SetTextRenderingMode(root, TextRenderingMode.Antialias);
        }

        /// <summary>Starts Avalonia headless with Skia, once per process.</summary>
        public static void Start<TApp>() where TApp : Application, new()
        {
            if (started)
                return;
            started = true;
            AppBuilder.Configure<TApp>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                // Dirty rectangles kept apart: two buttons far apart redraw two spots, not all between.
                .With(new Avalonia.Rendering.Composition.CompositionOptions { UseRegionDirtyRectClipping = true })
                .SetupWithoutStarting();
        }

        /// <summary>
        /// Runs Avalonia's queued work and renders the parts whose content
        /// changed; once per frame, however many callers ask.
        /// </summary>
        public static void Tick(long frame)
        {
            if (frame == tickedFrame)
                return;
            tickedFrame = frame;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        /// <summary>Sizes and places it for the screen; it starts rendering the first time.</summary>
        public void Place(Vector2 screen)
        {
            if (screen == placedFor)
                return;
            bool first = placedFor == Vector2.Zero;
            placedFor = screen;
            float scale = screen.Y / (float)DesignHeight * zoom;
            int width = (int)MathF.Ceiling(size.X * scale), height = (int)MathF.Ceiling(size.Y * scale);
            float left = corner switch
            {
                Corner.TopLeft or Corner.BottomLeft => offset.X * scale,
                Corner.TopCenter or Corner.BottomCenter => (screen.X - size.X * scale) * 0.5f + offset.X * scale,
                _ => screen.X - (offset.X + size.X) * scale,
            };
            float top = corner is Corner.BottomRight or Corner.BottomCenter or Corner.BottomLeft ? screen.Y - (offset.Y + size.Y) * scale : offset.Y * scale;
            rect = new RectangleF(MathF.Round(left), MathF.Round(top), width, height);
            scaler.LayoutTransform = new ScaleTransform(scale, scale);
            surface.Resize(width, height);
            if (first)
            {
                root.Prepare();
                root.StartRendering();
            }
        }

        /// <summary>Uploads the runs of rows Avalonia changed since the last upload; a new texture takes them all.</summary>
        public void Upload(CommandList commandList)
        {
            if (!Visible || !surface.Changed)
                return;
            int width = surface.Width, height = surface.Height, rowBytes = width * 4, row = 0;
            if (texture == null || texture.Width != width || texture.Height != height)
            {
                texture?.Dispose();
                texture = Texture.New2D(commandList.GraphicsDevice, width, height, Stride.Graphics.PixelFormat.B8G8R8A8_UNorm_SRgb, TextureFlags.ShaderResource);
                pending[0] = new bool[height];
                surface.ChangeAll();
            }
            surface.PassChanges(pending);
            while (NextRun(pending[0], ref row, out int first, out int rows))
                texture.SetData(commandList, new DataPointer(surface.Pixels + first * rowBytes, rows * rowBytes), 0, 0,
                                new ResourceRegion(0, first, 0, width, first + rows, 1));
        }

        // The next run of marked rows from row on, unmarked; runs a few rows apart join.
        static bool NextRun(bool[] marks, ref int row, out int first, out int rows)
        {
            const int Gap = 8;
            while (row < marks.Length && !marks[row])
                row++;
            (first, rows) = (row, 0);
            if (row >= marks.Length)
                return false;
            int end = row, quiet = 0;
            for (; row < marks.Length && quiet <= Gap; row++)
            {
                if (marks[row])
                    (end, quiet, marks[row]) = (row + 1, 0, false);
                else
                    quiet++;
            }
            rows = end - first;
            return true;
        }

        public void Draw(SpriteBatch batch, Vector2 screen)
        {
            if (!Visible || texture == null || Alpha <= 0f)
                return;
            float width = texture.Width * Zoom, height = texture.Height * Zoom;
            // Avalonia's pixels are premultiplied, so the tint fades all four channels.
            Color4 tint = (Color.White * Alpha).ToColor4();
            if (Angle != 0f)
            {
                // Rotated, the rectangle's corner is where the picture's centre goes.
                var centre = new RectangleF(rect.X + Shift.X + texture.Width * 0.5f, rect.Y + Shift.Y + texture.Height * 0.5f, width, height);
                batch.Draw(texture, centre, null, tint, Angle, new Vector2(texture.Width * 0.5f, texture.Height * 0.5f));
                return;
            }
            var at = new RectangleF(rect.X + Shift.X + (texture.Width - width) * 0.5f, rect.Y + Shift.Y + (texture.Height - height) * 0.5f, width, height);
            if (Reveal < 1f)
            {
                float share = Math.Max(0f, Reveal);
                at.Width *= share;
                batch.Draw(texture, at, new RectangleF(0f, 0f, texture.Width * share, texture.Height), tint, 0f, Vector2.Zero);
                return;
            }
            batch.Draw(texture, at, tint);
        }

        /// <summary>Whether the screen point lies on it.</summary>
        public bool Contains(Vector2 point) => Visible && rect.Contains(point.X, point.Y);

        /// <summary>The pointer moved to the screen point; held: the left button is down.</summary>
        public void Move(Vector2 point, bool held)
            => surface.Pointer(RawPointerEventType.Move, Local(point), held ? RawInputModifiers.LeftMouseButton : RawInputModifiers.None);

        /// <summary>The pointer left it: off its edge, so nothing stays under the pointer.</summary>
        public void Leave() => surface.Pointer(RawPointerEventType.Move, new Point(-10, -10), RawInputModifiers.None);

        public void Press(Vector2 point) => surface.Pointer(RawPointerEventType.LeftButtonDown, Local(point), RawInputModifiers.LeftMouseButton);

        public void Release(Vector2 point) => surface.Pointer(RawPointerEventType.LeftButtonUp, Local(point), RawInputModifiers.None);

        /// <summary>The wheel turned over the screen point, in notches (forward positive).</summary>
        public void Wheel(Vector2 point, float notches) => surface.Wheel(Local(point), notches);

        Point Local(Vector2 point) => new Point(point.X - rect.X, point.Y - rect.Y);

        public void Dispose()
        {
            root.StopRendering();
            root.Dispose();
            texture?.Dispose();
            texture = null;
        }

        /// <summary>The top level's platform side: one pixel buffer that keeps its frame, so Avalonia
        /// draws only its dirty rectangles into it (no layer of its own, no full copy), and pointer
        /// input. The rows a frame changed are found against a copy of the last one (Avalonia keeps its
        /// dirty rectangles to itself). Avalonia renders on the thread that ticks it, so no locks.</summary>
        sealed class Surface : OffscreenTopLevelImplBase, IFramebufferPlatformSurface
        {
            readonly Stopwatch clock = Stopwatch.StartNew();
            readonly MouseDevice mouse = new MouseDevice(new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true));
            byte[] buffer, last;
            bool[] changed;
            bool kept;

            public override IEnumerable<object> Surfaces => [this];

            public override IMouseDevice MouseDevice => mouse;

            /// <summary>The buffer's size in pixels and its pinned address.</summary>
            public int Width { get; private set; }
            public int Height { get; private set; }
            public IntPtr Pixels { get; private set; }

            public IFramebufferRenderTarget CreateFramebufferRenderTarget() => new KeptTarget(this);

            public void Resize(int width, int height)
            {
                Width = width;
                Height = height;
                // Pinned for good: Skia keeps drawing into the same address. A new buffer is drawn whole.
                buffer = GC.AllocateArray<byte>(width * height * 4, pinned: true);
                last = new byte[buffer.Length];
                changed = new bool[height];
                Pixels = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);
                kept = false;
                ClientSize = new Size(width, height);
            }

            public void Pointer(RawPointerEventType type, Point point, RawInputModifiers modifiers)
            {
                if (InputRoot != null)
                    Input?.Invoke(new RawPointerEventArgs(mouse, (ulong)clock.ElapsedMilliseconds, InputRoot, type, point, modifiers));
            }

            public void Wheel(Point point, float notches)
            {
                if (InputRoot != null)
                    Input?.Invoke(new RawMouseWheelEventArgs(mouse, (ulong)clock.ElapsedMilliseconds, InputRoot, point,
                                                             new Avalonia.Vector(0, notches), RawInputModifiers.None));
            }

            /// <summary>Whether rows changed since they were last taken.</summary>
            public bool Changed { get; private set; }

            public void ChangeAll()
            {
                Array.Fill(changed, true);
                Changed = true;
            }

            /// <summary>Marks every row changed since the last call in each of marks.</summary>
            public void PassChanges(bool[][] marks)
            {
                for (int y = 0; y < Height; y++)
                {
                    if (!changed[y])
                        continue;
                    changed[y] = false;
                    foreach (bool[] mark in marks)
                        mark[y] = true;
                }
                Changed = false;
            }

            ILockedFramebuffer Lock(out FramebufferLockProperties properties)
            {
                properties = new FramebufferLockProperties(kept);
                kept = true;
                return new Locked(this);
            }

            // After a frame: every row that differs from the last frame is marked and remembered.
            void Drawn()
            {
                int rowBytes = Width * 4;
                Span<byte> now = buffer, before = last;
                for (int y = 0; y < Height; y++)
                {
                    Span<byte> a = now.Slice(y * rowBytes, rowBytes), b = before.Slice(y * rowBytes, rowBytes);
                    if (a.SequenceEqual(b))
                        continue;
                    a.CopyTo(b);
                    changed[y] = true;
                    Changed = true;
                }
            }

            // Tells Avalonia the buffer keeps its frame between locks.
            sealed class KeptTarget(Surface surface) : IFramebufferRenderTargetWithProperties
            {
                public bool RetainsFrameContents => true;
                public ILockedFramebuffer Lock() => surface.Lock(out _);
                public ILockedFramebuffer Lock(out FramebufferLockProperties properties) => surface.Lock(out properties);
                public void Dispose() { }
            }

            sealed class Locked(Surface surface) : ILockedFramebuffer
            {
                public IntPtr Address => surface.Pixels;
                public PixelSize Size => new PixelSize(surface.Width, surface.Height);
                public int RowBytes => surface.Width * 4;
                public Vector Dpi => new Vector(96, 96);
                public AvaloniaPixelFormat Format => AvaloniaPixelFormat.Bgra8888;
                public void Dispose() => surface.Drawn();
            }
        }
    }
}
