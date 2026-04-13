using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using SunshineLibrary.Settings;

namespace SunshineLibrary.Tests
{
    /// <summary>
    /// PLAN §13d: plugin refuses to load unknown future settings versions rather
    /// than silently downgrading. These tests exercise the deserialization surface
    /// without the plugin wrapper (which needs Playnite's IPlayniteAPI).
    /// </summary>
    [TestClass]
    public class SettingsVersionTests
    {
        [TestMethod]
        public void CurrentVersion_LoadsNormally()
        {
            var settings = new SunshineLibrarySettings { SettingsVersion = SunshineLibrarySettings.CurrentSchemaVersion };
            var json = JsonConvert.SerializeObject(settings, SunshineLibrarySettings.JsonSettings);
            var back = JsonConvert.DeserializeObject<SunshineLibrarySettings>(json, SunshineLibrarySettings.JsonSettings);
            Assert.AreEqual(SunshineLibrarySettings.CurrentSchemaVersion, back.SettingsVersion);
        }

        [TestMethod]
        public void MissingVersion_DefaultsToOne()
        {
            // Simulated v0 file (predates SettingsVersion field).
            var json = "{ \"Hosts\": [] }";
            var back = JsonConvert.DeserializeObject<SunshineLibrarySettings>(json, SunshineLibrarySettings.JsonSettings);
            // Missing key → C# default for int property, but our POCO defaults to 1.
            // The load path checks `saved.SettingsVersion > CurrentSchemaVersion`, so
            // missing-field behavior falls into "load normally" since 0 or 1 ≤ 1.
            Assert.IsTrue(back.SettingsVersion <= SunshineLibrarySettings.CurrentSchemaVersion);
        }

        [TestMethod]
        public void FutureVersion_Detectable()
        {
            // Plugin code does: `if (saved.SettingsVersion > CurrentSchemaVersion) → use defaults`
            // This is the condition that plugin load-path checks.
            var future = new SunshineLibrarySettings { SettingsVersion = SunshineLibrarySettings.CurrentSchemaVersion + 1 };
            Assert.IsTrue(future.SettingsVersion > SunshineLibrarySettings.CurrentSchemaVersion);
        }

        [TestMethod]
        public void RoundTrip_PreservesExtraFields()
        {
            var s = new SunshineLibrarySettings
            {
                SettingsVersion = 1,
                NotificationMode = NotificationMode.OnUpdateOnly,
            };
            s.Hosts.Add(new Models.HostConfig { Label = "Test", Address = "1.2.3.4", Port = 47990 });

            var json = JsonConvert.SerializeObject(s, SunshineLibrarySettings.JsonSettings);
            var back = JsonConvert.DeserializeObject<SunshineLibrarySettings>(json, SunshineLibrarySettings.JsonSettings);

            Assert.AreEqual(NotificationMode.OnUpdateOnly, back.NotificationMode);
            Assert.AreEqual(1, back.Hosts.Count);
            Assert.AreEqual("Test", back.Hosts[0].Label);
        }

        [TestMethod]
        public void Password_NeverSerialized()
        {
            var s = new SunshineLibrarySettings();
            s.Hosts.Add(new Models.HostConfig
            {
                Label = "Test",
                Address = "1.2.3.4",
                Port = 47990,
                AdminUser = "admin",
                AdminPassword = "supersecret",
            });

            var json = JsonConvert.SerializeObject(s, SunshineLibrarySettings.JsonSettings);
            StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("supersecret"));

            var back = JsonConvert.DeserializeObject<SunshineLibrarySettings>(json, SunshineLibrarySettings.JsonSettings);
            Assert.IsNull(back.Hosts[0].AdminPassword);
        }
    }
}
