using CardboardXR.Demos;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    // Which of the phone's apps open in landscape: those whose launcher activity can turn, unless the
    // switch is off. Upright-only apps open upright, and landscape apps need no help.
    public class AndroidAppsTests
    {
        // ActivityInfo.screenOrientation values.
        const int k_Unspecified = -1;
        const int k_Landscape = 0;
        const int k_Portrait = 1;
        const int k_User = 2;
        const int k_Behind = 3;
        const int k_Sensor = 4;
        const int k_NoSensor = 5;
        const int k_SensorLandscape = 6;
        const int k_SensorPortrait = 7;
        const int k_ReverseLandscape = 8;
        const int k_ReversePortrait = 9;
        const int k_FullSensor = 10;
        const int k_UserLandscape = 11;
        const int k_UserPortrait = 12;
        const int k_FullUser = 13;
        const int k_Locked = 14;

        [Test]
        public void AppsThatCanTurnOpenInLandscape()
        {
            foreach (var orientation in new[] { k_Unspecified, k_User, k_Behind, k_Sensor, k_NoSensor, k_FullSensor, k_FullUser, k_Locked })
            {
                var fit = AndroidApps.FitOf(orientation);
                Assert.AreEqual(AndroidApps.Fit.Turns, fit, $"screenOrientation {orientation}");
                Assert.IsTrue(AndroidApps.HoldsLandscape(fit, true), $"screenOrientation {orientation}");
                Assert.IsFalse(AndroidApps.HoldsLandscape(fit, false), $"screenOrientation {orientation}, switched off");
            }
        }

        [Test]
        public void UprightOnlyAppsOpenUpright()
        {
            foreach (var orientation in new[] { k_Portrait, k_SensorPortrait, k_ReversePortrait, k_UserPortrait })
            {
                var fit = AndroidApps.FitOf(orientation);
                Assert.AreEqual(AndroidApps.Fit.Portrait, fit, $"screenOrientation {orientation}");
                Assert.IsFalse(AndroidApps.HoldsLandscape(fit, true), $"screenOrientation {orientation}");
            }
        }

        [Test]
        public void LandscapeAppsNeedNoHelp()
        {
            foreach (var orientation in new[] { k_Landscape, k_SensorLandscape, k_ReverseLandscape, k_UserLandscape })
            {
                var fit = AndroidApps.FitOf(orientation);
                Assert.AreEqual(AndroidApps.Fit.Landscape, fit, $"screenOrientation {orientation}");
                Assert.IsFalse(AndroidApps.HoldsLandscape(fit, true), $"screenOrientation {orientation}");
            }
        }

        [Test]
        public void TheTaglineSaysHowAnAppOpens()
        {
            var turns = new AndroidApps.App("com.example.player/com.example.player.Main", "Player", k_Unspecified);
            StringAssert.StartsWith("Player", AndroidApps.Describe(turns, true));
            StringAssert.Contains("landscape", AndroidApps.Describe(turns, true));
            StringAssert.DoesNotContain("landscape", AndroidApps.Describe(turns, false));
            var upright = new AndroidApps.App("com.example.chat/com.example.chat.Main", "Chat", k_Portrait);
            StringAssert.Contains("upright", AndroidApps.Describe(upright, true));
        }

        [Test]
        public void OnlyOnAnAndroidPhone()
        {
            // The tests run in the Editor, which has no phone's apps, so the library has no Android section.
            Assert.IsFalse(AndroidApps.Supported);
        }
    }
}
