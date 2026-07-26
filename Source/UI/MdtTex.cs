using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>Shipped UI icons (loaded on the main thread). Null-safe: callers fall back to
    /// line-drawn shapes if a texture is somehow missing.</summary>
    [StaticConstructorOnStartup]
    public static class MdtTex
    {
        public static readonly Texture2D Pin = ContentFinder<Texture2D>.Get("UI/DevTools/Pin", false);
        public static readonly Texture2D Chevron = ContentFinder<Texture2D>.Get("UI/DevTools/Chevron", false);
        public static readonly Texture2D Close = ContentFinder<Texture2D>.Get("UI/DevTools/Close", false);
    }
}
