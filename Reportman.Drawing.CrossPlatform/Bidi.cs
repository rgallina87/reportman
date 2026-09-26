using System;
using System.Runtime.InteropServices;

namespace Reportman.Drawing
{
    /// <summary>
    /// The bidirectional analysis this engine needs from ICU: paragraph levels, logical
    /// levels per character and visual runs. Two implementations behind it: icu.net (Windows,
    /// Linux, macOS — the versioned libicuuc/libicui18n) and, on Android, the platform's own
    /// ICU4C through <c>libicu.so</c>, the stable C API Android exports to apps since API 31
    /// (public.libraries.txt). icu.net cannot run on Android at all (its platform detection
    /// throws on "Android (API level N)"), and shipping ICU would add ~30 MB per ABI for a
    /// library the device already has.
    /// </summary>
    internal interface IBidiPara : IDisposable
    {
        /// <summary>Analyzes a paragraph. 254 (UBIDI_DEFAULT_LTR) = direction from the first strong character, LTR without one;
        /// 255 (UBIDI_DEFAULT_RTL) would make a line of only digits and signs RTL.</summary>
        void SetPara(string text, byte paraLevel);
        /// <summary>The resolved embedding level of the character at <paramref name="charIndex"/>.</summary>
        byte GetLevelAt(int charIndex);
        /// <summary>Number of visual runs of the paragraph.</summary>
        int CountRuns();
        /// <summary>The <paramref name="runIndex"/>-th visual run; returns true when it is right-to-left.</summary>
        bool GetVisualRun(int runIndex, out int logicalStart, out int length);
        /// <summary>The paragraph level (odd = RTL paragraph).</summary>
        byte GetParaLevel();
    }

    /// <summary>Picks the BiDi implementation for this platform.</summary>
    internal static class BidiFactory
    {
        public static IBidiPara Create()
        {
#if !NETFRAMEWORK
            if (IcuNative.Available)
                return new BidiNative();
#endif
            return new BidiIcuNet();
        }
    }

    /// <summary>BiDi through icu.net.</summary>
    internal sealed class BidiIcuNet : IBidiPara
    {
        private readonly Icu.BiDi bidi;
        private static bool initialized;

        public BidiIcuNet()
        {
            if (!initialized)
            {
                Icu.Wrapper.Init();
                initialized = true;
            }
            bidi = new Icu.BiDi();
        }
        public void SetPara(string text, byte paraLevel) { bidi.SetPara(text, paraLevel, null); }
        public byte GetLevelAt(int charIndex) { return bidi.GetLevelAt(charIndex); }
        public int CountRuns() { return bidi.CountRuns(); }
        public bool GetVisualRun(int runIndex, out int logicalStart, out int length)
        {
            var dir = bidi.GetVisualRun(runIndex, out logicalStart, out length);
            return dir.ToString().Contains("RTL");
        }
        public byte GetParaLevel() { return bidi.GetParaLevel(); }
        public void Dispose() { bidi.Dispose(); }
    }

#if !NETFRAMEWORK
    /// <summary>
    /// The handful of ICU4C entry points the engine uses, bound by function pointer to the
    /// library Android exports to apps (<c>libicu.so</c>, unversioned symbols). Only probed on
    /// Android; elsewhere <see cref="Available"/> is false and icu.net is used.
    /// </summary>
    internal static unsafe class IcuNative
    {
        private static bool probed;
        private static bool available;
        private static readonly object gate = new object();

        internal static delegate* unmanaged<IntPtr> ubidi_open;
        internal static delegate* unmanaged<IntPtr, void> ubidi_close;
        internal static delegate* unmanaged<IntPtr, ushort*, int, byte, byte*, int*, void> ubidi_setPara;
        internal static delegate* unmanaged<IntPtr, int, byte> ubidi_getLevelAt;
        internal static delegate* unmanaged<IntPtr, int*, int> ubidi_countRuns;
        internal static delegate* unmanaged<IntPtr, int, int*, int*, int> ubidi_getVisualRun;
        internal static delegate* unmanaged<IntPtr, byte> ubidi_getParaLevel;

        /// <summary>The ICU major version the platform reports (0 when unknown).</summary>
        public static int Version { get; private set; }

        public static bool Available
        {
            get
            {
                if (probed)
                    return available;
                lock (gate)
                {
                    if (probed)
                        return available;
                    probed = true;
                    available = Probe();
                    return available;
                }
            }
        }

        private static bool Probe()
        {
            if (!OperatingSystem.IsAndroid())
                return false;
            try
            {
                if (!NativeLibrary.TryLoad("libicu.so", out IntPtr lib) || lib == IntPtr.Zero)
                    return false;
                IntPtr p;
                if (!NativeLibrary.TryGetExport(lib, "ubidi_open", out p)) return false;
                ubidi_open = (delegate* unmanaged<IntPtr>)p;
                if (!NativeLibrary.TryGetExport(lib, "ubidi_close", out p)) return false;
                ubidi_close = (delegate* unmanaged<IntPtr, void>)p;
                if (!NativeLibrary.TryGetExport(lib, "ubidi_setPara", out p)) return false;
                ubidi_setPara = (delegate* unmanaged<IntPtr, ushort*, int, byte, byte*, int*, void>)p;
                if (!NativeLibrary.TryGetExport(lib, "ubidi_getLevelAt", out p)) return false;
                ubidi_getLevelAt = (delegate* unmanaged<IntPtr, int, byte>)p;
                if (!NativeLibrary.TryGetExport(lib, "ubidi_countRuns", out p)) return false;
                ubidi_countRuns = (delegate* unmanaged<IntPtr, int*, int>)p;
                if (!NativeLibrary.TryGetExport(lib, "ubidi_getVisualRun", out p)) return false;
                ubidi_getVisualRun = (delegate* unmanaged<IntPtr, int, int*, int*, int>)p;
                if (!NativeLibrary.TryGetExport(lib, "ubidi_getParaLevel", out p)) return false;
                ubidi_getParaLevel = (delegate* unmanaged<IntPtr, byte>)p;
                if (NativeLibrary.TryGetExport(lib, "u_getVersion", out p) && p != IntPtr.Zero)
                {
                    byte* v = stackalloc byte[4];
                    ((delegate* unmanaged<byte*, void>)p)(v);
                    Version = v[0];
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>BiDi on the platform ICU (Android). The text is pinned for as long as ICU may look at it.</summary>
    internal sealed unsafe class BidiNative : IBidiPara
    {
        private IntPtr handle;
        private GCHandle pin;

        public BidiNative()
        {
            handle = IcuNative.ubidi_open();
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("ubidi_open failed");
        }

        public void SetPara(string text, byte paraLevel)
        {
            Unpin();
            // ubidi_setPara keeps a pointer to the text: it must stay put until the next paragraph.
            pin = GCHandle.Alloc(text, GCHandleType.Pinned);
            int err = 0;
            IcuNative.ubidi_setPara(handle, (ushort*)pin.AddrOfPinnedObject(), text.Length, paraLevel, null, &err);
            if (err > 0)
                throw new InvalidOperationException("ubidi_setPara failed, UErrorCode " + err);
        }

        public byte GetLevelAt(int charIndex) { return IcuNative.ubidi_getLevelAt(handle, charIndex); }

        public int CountRuns()
        {
            int err = 0;
            int n = IcuNative.ubidi_countRuns(handle, &err);
            if (err > 0)
                throw new InvalidOperationException("ubidi_countRuns failed, UErrorCode " + err);
            return n;
        }

        public bool GetVisualRun(int runIndex, out int logicalStart, out int length)
        {
            int start, len;
            int dir = IcuNative.ubidi_getVisualRun(handle, runIndex, &start, &len);   // UBIDI_LTR = 0, UBIDI_RTL = 1
            logicalStart = start;
            length = len;
            return dir == 1;
        }

        public byte GetParaLevel() { return IcuNative.ubidi_getParaLevel(handle); }

        private void Unpin()
        {
            if (pin.IsAllocated)
                pin.Free();
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                IcuNative.ubidi_close(handle);
                handle = IntPtr.Zero;
            }
            Unpin();
        }
    }
#endif
}
