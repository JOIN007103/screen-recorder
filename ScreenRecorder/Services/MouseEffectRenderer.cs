using System;
using System.Collections.Generic;
using ScreenRecorder.Models;
using ScreenRecorder.Native;

namespace ScreenRecorder.Services
{
    /// <summary>
    /// 鼠标效果渲染器：在捕获的 BGRA 帧像素上直接绘制三项鼠标效果。
    /// 效果录进视频（而非屏幕叠加层），不干扰用户实际操作。
    /// 三项效果：点击涟漪、光标高亮、鼠标轨迹（均由冻结决策纳入 MVP）。
    /// </summary>
    internal sealed class MouseEffectRenderer
    {
        private readonly RecorderSettings _settings;
        private readonly int _width;
        private readonly int _height;
        private readonly List<Click> _clicks = new();
        private readonly LinkedList<Tip> _trail = new();
        private readonly object _sync = new();
        private static readonly TimeSpan RippleDuration = TimeSpan.FromMilliseconds(600);
        private const int TrailMax = 24;
        private const int TrailLifeMs = 900;

        private struct Click
        {
            public int X, Y;
            public bool Right;
            public DateTime Time;
        }

        private struct Tip
        {
            public int X, Y;
            public DateTime Time;
        }

        public MouseEffectRenderer(RecorderSettings settings, int width, int height)
        {
            _settings = settings;
            _width = width;
            _height = height;
        }

        /// <summary>记录一次点击事件（由全局鼠标钩子回调）。</summary>
        public void AddClick(int x, int y, bool right)
        {
            if (!_settings.ShowClickRipple) return;
            lock (_sync)
            {
                _clicks.Add(new Click { X = x, Y = y, Right = right, Time = DateTime.Now });
                if (_clicks.Count > 16) _clicks.RemoveAt(0);
            }
        }

        /// <summary>
        /// 在当前帧上叠加所有启用的效果。
        /// </summary>
        public void Apply(byte[] bgra, int width, int height, DateTime now)
        {
            // 鼠标轨迹：记录当前位置
            if (_settings.ShowMouseTrail && NativeMethods.GetCursorPos(out var pos))
            {
                lock (_sync)
                {
                    _trail.AddLast(new Tip { X = pos.X, Y = pos.Y, Time = now });
                    while (_trail.Count > TrailMax) _trail.RemoveFirst();
                    RemoveExpiredTips(now);
                }
                DrawTrail(bgra, width, height, now);
            }

            // 光标高亮
            if (_settings.ShowCursorHighlight && NativeMethods.GetCursorPos(out var cp))
                DrawHighlight(bgra, width, height, cp.X, cp.Y);

            // 点击涟漪
            if (_settings.ShowClickRipple)
                DrawRipples(bgra, width, height, now);
        }

        private void RemoveExpiredTips(DateTime now)
        {
            while (_trail.First != null && (now - _trail.First.Value.Time).TotalMilliseconds > TrailLifeMs)
                _trail.RemoveFirst();
        }

        // ---------- 效果绘制 ----------

        private void DrawHighlight(byte[] buf, int w, int h, int cx, int cy)
        {
            uint color = _settings.HighlightColorBgr;
            DrawCircleOutline(buf, w, h, cx, cy, 14, 2, color, 255);
            // 中心小点，标记精确位置
            DrawFilledCircle(buf, w, h, cx, cy, 3, color, 255);
        }

        private void DrawRipples(byte[] buf, int w, int h, DateTime now)
        {
            lock (_sync)
            {
                for (int i = _clicks.Count - 1; i >= 0; i--)
                {
                    var c = _clicks[i];
                    double ageMs = (now - c.Time).TotalMilliseconds;
                    if (ageMs > RippleDuration.TotalMilliseconds)
                    {
                        _clicks.RemoveAt(i);
                        continue;
                    }
                    double t = ageMs / RippleDuration.TotalMilliseconds; // 0→1
                    int radius = 8 + (int)(t * 34);
                    int alpha = (int)(255 * (1 - t));
                    uint color = c.Right ? 0x0000FF : _settings.RippleColorBgr; // 右键红色(BGR)
                    DrawCircleOutline(buf, w, h, c.X, c.Y, radius, 3, color, alpha);
                }
            }
        }

        private void DrawTrail(byte[] buf, int w, int h, DateTime now)
        {
            LinkedList<Tip> snapshot;
            lock (_sync)
            {
                snapshot = new LinkedList<Tip>(_trail);
            }
            var node = snapshot.First;
            while (node?.Next != null)
            {
                var a = node.Value;
                var b = node.Next.Value;
                double ageMs = (now - a.Time).TotalMilliseconds;
                double alpha = 200 * (1 - ageMs / TrailLifeMs);
                if (alpha > 0)
                    DrawLine(buf, w, h, a.X, a.Y, b.X, b.Y, 2, _settings.RippleColorBgr, (int)alpha);
                node = node.Next;
            }
        }

        // ---------- 基础光栅绘制（直接操作 BGRA 字节）----------

        private static void SetPixel(byte[] buf, int w, int h, int x, int y, uint bgr, int alpha)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int idx = (y * w + x) * 4;
            byte b = (byte)(bgr & 0xFF);
            byte g = (byte)((bgr >> 8) & 0xFF);
            byte r = (byte)((bgr >> 16) & 0xFF);
            if (alpha >= 255)
            {
                buf[idx] = b; buf[idx + 1] = g; buf[idx + 2] = r;
            }
            else
            {
                double a = alpha / 255.0;
                buf[idx] = (byte)(buf[idx] * (1 - a) + b * a);
                buf[idx + 1] = (byte)(buf[idx + 1] * (1 - a) + g * a);
                buf[idx + 2] = (byte)(buf[idx + 2] * (1 - a) + r * a);
            }
        }

        private static void DrawFilledCircle(byte[] buf, int w, int h, int cx, int cy, int radius, uint color, int alpha)
        {
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dy * dy <= radius * radius)
                        SetPixel(buf, w, h, cx + dx, cy + dy, color, alpha);
        }

        private static void DrawCircleOutline(byte[] buf, int w, int h, int cx, int cy, int radius, int thickness, uint color, int alpha)
        {
            int inner = Math.Max(0, radius - thickness);
            int outer2 = radius * radius, inner2 = inner * inner;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int d2 = dx * dx + dy * dy;
                    if (d2 <= outer2 && d2 >= inner2)
                        SetPixel(buf, w, h, cx + dx, cy + dy, color, alpha);
                }
            }
        }

        private static void DrawLine(byte[] buf, int w, int h, int x0, int y0, int x1, int y1, int thickness, uint color, int alpha)
        {
            int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            int x = x0, y = y0;
            int guard = 0;
            while (guard++ < 8000)
            {
                DrawFilledCircle(buf, w, h, x, y, thickness / 2 + 1, color, alpha);
                if (x == x1 && y == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x += sx; }
                if (e2 < dx) { err += dx; y += sy; }
            }
        }
    }
}
