using Microsoft.VisualStudio.TestTools.UnitTesting;
using SunshineLibrary.Models;
using SunshineLibrary.Services.Clients;
using System;
using System.Linq;

namespace SunshineLibrary.Tests
{
    [TestClass]
    public class MoonlightClientBuildLaunchTests
    {
        private static HostConfig Host() => new HostConfig
        {
            Id = Guid.NewGuid(),
            Label = "Test",
            Address = "192.168.1.5",
            Port = 47990,
            ServerType = ServerType.Sunshine,
        };

        private static RemoteApp App(string name = "Cyberpunk 2077") =>
            new RemoteApp { Name = name, StableId = "abc", Index = 0 };

        [TestMethod]
        public void Defaults_IncludeQuitAfter_AndStreamVerb()
        {
            var args = MoonlightClient.ComposeArgs(Host(), App(), StreamOverrides.BuiltinDefault, ClientDisplayInfo.Unknown);
            Assert.AreEqual("stream", args[0]);
            Assert.AreEqual("192.168.1.5", args[1]);
            Assert.AreEqual("Cyberpunk 2077", args[2]);
            CollectionAssert.Contains(args, "--quit-after");
        }

        [TestMethod]
        public void AutoResolution_UsesDetectedDisplay()
        {
            var display = new ClientDisplayInfo { Width = 2560, Height = 1600, RefreshHz = 120, HdrEnabled = false };
            var overrides = StreamOverrides.BuiltinDefault; // Auto / Auto / Auto
            var args = MoonlightClient.ComposeArgs(Host(), App(), overrides, display);

            Assert.AreEqual("2560x1600", ArgAfter(args, "--resolution"));
            Assert.AreEqual("120", ArgAfter(args, "--fps"));
            CollectionAssert.Contains(args, "--no-hdr");
        }

        [TestMethod]
        public void StaticResolution_OverridesAuto()
        {
            var display = new ClientDisplayInfo { Width = 1280, Height = 800, RefreshHz = 60, HdrEnabled = false };
            var overrides = new StreamOverrides
            {
                ResolutionMode = ResolutionMode.Static,
                ResolutionStatic = "1920x1080",
                FpsMode = FpsMode.Static,
                FpsStatic = 90,
                Hdr = HdrMode.On,
            };
            var merged = StreamOverrides.BuiltinDefault.MergedWith(overrides);
            var args = MoonlightClient.ComposeArgs(Host(), App(), merged, display);

            Assert.AreEqual("1920x1080", ArgAfter(args, "--resolution"));
            Assert.AreEqual("90", ArgAfter(args, "--fps"));
            CollectionAssert.Contains(args, "--hdr");
        }

        [TestMethod]
        public void UnknownDisplay_OmitsResolutionAndFps()
        {
            var args = MoonlightClient.ComposeArgs(Host(), App(), StreamOverrides.BuiltinDefault, ClientDisplayInfo.Unknown);
            Assert.IsFalse(args.Contains("--resolution"));
            Assert.IsFalse(args.Contains("--fps"));
            // HDR is also omitted (Auto + unknown display)
            Assert.IsFalse(args.Contains("--hdr"));
            Assert.IsFalse(args.Contains("--no-hdr"));
        }

        [TestMethod]
        public void BitrateOutOfRange_IsClamped()
        {
            var overrides = new StreamOverrides { BitrateKbps = 9999999 };
            var merged = StreamOverrides.BuiltinDefault.MergedWith(overrides);
            var args = MoonlightClient.ComposeArgs(Host(), App(), merged, ClientDisplayInfo.Unknown);
            Assert.AreEqual("500000", ArgAfter(args, "--bitrate"));
        }

        [TestMethod]
        public void MergeOrder_OverrideBeatsDefault_NullInherits()
        {
            var global = new StreamOverrides { Hdr = HdrMode.Off, BitrateKbps = 20000 };
            var perGame = new StreamOverrides { Hdr = HdrMode.On /* BitrateKbps left null */ };
            var merged = StreamOverrides.BuiltinDefault.MergedWith(global).MergedWith(perGame);

            Assert.AreEqual(HdrMode.On, merged.Hdr);
            Assert.AreEqual(20000, merged.BitrateKbps);
        }

        private static string ArgAfter(System.Collections.Generic.List<string> args, string flag)
        {
            int i = args.IndexOf(flag);
            return (i >= 0 && i + 1 < args.Count) ? args[i + 1] : null;
        }
    }
}
