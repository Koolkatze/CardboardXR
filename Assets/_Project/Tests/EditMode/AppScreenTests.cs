using CardboardXR.Demos;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    // A phone app's screen in XR: the shape of its display, and where on the display a look lands.
    public class AppScreenTests
    {
        [Test]
        public void TheScreenHasItsDisplaysShape()
        {
            foreach (var landscape in new[] { true, false })
            {
                var display = AppScreen.DisplayPixels(landscape);
                var screen = AppScreen.ScreenDp(landscape);
                Assert.AreEqual(landscape, display.x > display.y, "display");
                Assert.AreEqual((float)display.x / display.y, screen.x / screen.y, 1e-4f, "screen");
                // The panel holds the screen, with the bar under it.
                var panel = AppScreen.PanelDp(landscape);
                Assert.Greater(panel.x, screen.x);
                Assert.Greater(panel.y, screen.y + 48f);
            }
        }

        [Test]
        public void ALookLandsWhereItIsOnTheDisplay()
        {
            var display = AppScreen.DisplayPixels(true);
            // The screen's fractions run from its bottom-left; the display's pixels from its top-left.
            Assert.AreEqual(new Vector2(0f, display.y), AppScreen.ToDisplay(new Vector2(0f, 0f), display));
            Assert.AreEqual(new Vector2(display.x, 0f), AppScreen.ToDisplay(new Vector2(1f, 1f), display));
            Assert.AreEqual(new Vector2(display.x / 2f, display.y / 2f), AppScreen.ToDisplay(new Vector2(0.5f, 0.5f), display));
            Assert.AreEqual(new Vector2(display.x * 0.25f, display.y * 0.25f), AppScreen.ToDisplay(new Vector2(0.25f, 0.75f), display));
            // Off the screen, held to its edge, so a drag that runs off keeps going along it.
            Assert.AreEqual(new Vector2(0f, 0f), AppScreen.ToDisplay(new Vector2(-0.3f, 1.4f), display));
        }

        [Test]
        public void WithoutShizukuTheScreenSaysWhatToDo()
        {
            StringAssert.Contains("isn't installed", AppScreen.NoticeFor(AppScreen.ShizukuState.Missing, false, "Player", ""));
            StringAssert.Contains("isn't running", AppScreen.NoticeFor(AppScreen.ShizukuState.Missing, true, "Player", ""));
            StringAssert.Contains("Allow CardboardXR", AppScreen.NoticeFor(AppScreen.ShizukuState.NotAllowed, true, "Player", ""));
            StringAssert.Contains("Update Shizuku", AppScreen.NoticeFor(AppScreen.ShizukuState.TooOld, true, "Player", ""));
            StringAssert.Contains("binder died", AppScreen.NoticeFor(AppScreen.ShizukuState.Error, true, "Player", "binder died"));
            foreach (AppScreen.ShizukuState state in System.Enum.GetValues(typeof(AppScreen.ShizukuState)))
                StringAssert.Contains("Player", AppScreen.NoticeFor(state, true, "Player", "x"));
        }

        [Test]
        public void AppsThatCanTurnGetALandscapeScreen()
        {
            Assert.IsTrue(AndroidApps.OpensInLandscape(AndroidApps.Fit.Turns, true));
            Assert.IsFalse(AndroidApps.OpensInLandscape(AndroidApps.Fit.Turns, false));
            Assert.IsTrue(AndroidApps.OpensInLandscape(AndroidApps.Fit.Landscape, false));
            Assert.IsFalse(AndroidApps.OpensInLandscape(AndroidApps.Fit.Portrait, true));
        }
    }
}
