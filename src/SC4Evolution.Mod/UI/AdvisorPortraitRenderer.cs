using System;
using System.IO;
using SC4Evolution.Core;
using UnityEngine;

namespace SC4Evolution.Mod.UI
{
    /// <summary>Loads locally generated SC4 first-frame projections. Call only on Unity's UI thread.</summary>
    public static class AdvisorPortraitRenderer
    {
        public static Texture2D Load(string projectRoot, AdvisorId advisor, AdvisorMood mood)
        {
            if (String.IsNullOrEmpty(projectRoot) || !Enum.IsDefined(typeof(AdvisorId), advisor)) return null;
            string state;
            switch (mood)
            {
                case AdvisorMood.Pleased: state = "happy"; break;
                case AdvisorMood.Concerned: state = "attention"; break;
                case AdvisorMood.Angry: state = "alarmed"; break;
                default: state = "neutral"; break;
            }
            try
            {
                string folder = Path.Combine(Path.Combine(Path.Combine(projectRoot, "analysis"), "generated"), "sc4-advisor-assets");
                string path = Path.Combine(folder, advisor.ToString() + "-" + state + ".png");
                return LoadPath(path, "SC4Evolution.Advisor." + advisor + "." + state);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (UnityException) { return null; }
        }

        /// <summary>Loads the decoded ANIM frames emitted by SC4AdvisorAssets.</summary>
        public static AdvisorPortraitAnimation LoadAnimation(string projectRoot, AdvisorId advisor, AdvisorMood mood)
        {
            if (String.IsNullOrEmpty(projectRoot) || !Enum.IsDefined(typeof(AdvisorId), advisor)) return null;
            string state;
            switch (mood)
            {
                case AdvisorMood.Pleased: state = "happy"; break;
                case AdvisorMood.Concerned: state = "attention"; break;
                case AdvisorMood.Angry: state = "alarmed"; break;
                default: state = "neutral"; break;
            }
            try
            {
                string folder = Path.Combine(Path.Combine(Path.Combine(projectRoot, "analysis"), "generated"), "sc4-advisor-assets");
                System.Collections.Generic.List<Texture2D> frames = new System.Collections.Generic.List<Texture2D>();
                for (int i = 0; i < 120; i++)
                {
                    string path = Path.Combine(folder, advisor.ToString() + "-" + state + "-f" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".png");
                    if (!File.Exists(path)) break;
                    Texture2D frame = LoadPath(path, "SC4Evolution.Advisor." + advisor + "." + state + "." + i);
                    if (frame == null) break;
                    frames.Add(frame);
                }
                if (frames.Count == 0)
                {
                    Texture2D fallback = Load(projectRoot, advisor, mood);
                    if (fallback != null) frames.Add(fallback);
                }
                int rate = advisor == AdvisorId.Finance && (mood == AdvisorMood.Calm || mood == AdvisorMood.Pleased) ? 30 : 10;
                return frames.Count == 0 ? null : new AdvisorPortraitAnimation(frames.ToArray(), frames.Count == 1 ? 1 : rate);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (UnityException) { return null; }
        }

        private static Texture2D LoadPath(string path, string name)
        {
            Texture2D texture = null;
            try
            {
                if (!File.Exists(path)) return null;
                var file = new FileInfo(path);
                if (file.Length < 33 || file.Length > 2 * 1024 * 1024) return null;
                byte[] bytes = File.ReadAllBytes(path);
                byte[] magic = { 137, 80, 78, 71, 13, 10, 26, 10 };
                for (int i = 0; i < magic.Length; i++) if (bytes[i] != magic[i]) return null;
                // Accept only our fixed 384x384 RGBA8 PNG profile, before invoking Unity's decoder.
                if (bytes[8] != 0 || bytes[9] != 0 || bytes[10] != 0 || bytes[11] != 13 ||
                    bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82 ||
                    bytes[16] != 0 || bytes[17] != 0 || bytes[18] != 1 || bytes[19] != 128 ||
                    bytes[20] != 0 || bytes[21] != 0 || bytes[22] != 1 || bytes[23] != 128 ||
                    bytes[24] != 8 || bytes[25] != 6 || bytes[26] != 0 || bytes[27] != 0 || bytes[28] != 0) return null;
                texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                texture.name = name;
                if (!texture.LoadImage(bytes) || texture.width != 384 || texture.height != 384)
                {
                    Release(texture);
                    return null;
                }
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                return texture;
            }
            catch (IOException) { Release(texture); return null; }
            catch (UnauthorizedAccessException) { Release(texture); return null; }
            catch (ArgumentException) { Release(texture); return null; }
            catch (UnityException) { Release(texture); return null; }
        }

        public static void Release(Texture2D texture)
        {
            if (texture != null) UnityEngine.Object.Destroy(texture);
        }
    }

    public sealed class AdvisorPortraitAnimation
    {
        private readonly Texture2D[] _frames;
        private readonly int _framesPerSecond;
        private readonly float _startedAt;

        internal AdvisorPortraitAnimation(Texture2D[] frames, int framesPerSecond)
        {
            if (frames == null || frames.Length == 0 || framesPerSecond <= 0) throw new ArgumentException("Portrait animation requires frames and a positive rate.");
            _frames = frames;
            _framesPerSecond = framesPerSecond;
            _startedAt = Time.realtimeSinceStartup;
        }

        public int FrameCount { get { return _frames.Length; } }
        public Texture2D Current { get { return CurrentAt(Time.realtimeSinceStartup); } }
        public Texture2D CurrentAt(float time)
        {
            if (_frames.Length == 1) return _frames[0];
            int index = Mathf.Abs((int)Mathf.Floor(Mathf.Max(0f, time - _startedAt) * _framesPerSecond)) % _frames.Length;
            return _frames[index];
        }
        public void Release()
        {
            for (int i = 0; i < _frames.Length; i++) AdvisorPortraitRenderer.Release(_frames[i]);
        }
    }
}
