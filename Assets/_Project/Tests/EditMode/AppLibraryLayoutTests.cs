using CardboardXR.Demos;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    // The app library pages instead of growing taller, so the settings panel below it stays put.
    public class AppLibraryLayoutTests
    {
        [Test]
        public void SixAppsFitOnOnePageAsBefore()
        {
            Assert.AreEqual(1, AppLibrary.Pages(6));
            // The lobby and five demos: 3 by 2, with no arrows or dots.
            Assert.AreEqual(new Vector2(432f, 324f), AppLibrary.Size(6));
            Assert.AreEqual(AppLibrary.Size(4).y, AppLibrary.Size(6).y);
            Assert.Less(AppLibrary.Size(3).y, AppLibrary.Size(4).y);
        }

        [Test]
        public void MoreAppsTurnPagesInsteadOfGrowing()
        {
            Assert.AreEqual(2, AppLibrary.Pages(7));
            Assert.AreEqual(2, AppLibrary.Pages(12));
            Assert.AreEqual(3, AppLibrary.Pages(13));
            Assert.AreEqual(AppLibrary.Size(7), AppLibrary.Size(30));
            // Only the page dots' row taller, and the arrows' width wider.
            Assert.AreEqual(14f, AppLibrary.Size(7).y - AppLibrary.Size(6).y, 1e-4f);
            Assert.AreEqual(2f * (56f + 8f), AppLibrary.Size(7).x - AppLibrary.Size(6).x, 1e-4f);
        }

        [Test]
        public void AppsOnLaterPagesTakeTheSamePlaces()
        {
            Assert.AreEqual(0, AppLibrary.PageOf(5));
            Assert.AreEqual(1, AppLibrary.PageOf(6));
            Assert.AreEqual(2, AppLibrary.PageOf(12));
            for (var slot = 0; slot < 6; slot++)
                Assert.AreEqual(AppLibrary.CellPosition(slot, 13), AppLibrary.CellPosition(slot + 12, 13));
        }

        [Test]
        public void CellsStayInsideAndClearOfTheArrows()
        {
            foreach (var apps in new[] { 1, 6, 7, 13 })
            {
                var size = AppLibrary.Size(apps);
                var inner = size.x / 2f - 16f - (AppLibrary.Pages(apps) > 1 ? 56f + 8f : 0f);
                for (var i = 0; i < apps; i++)
                {
                    var cell = AppLibrary.CellPosition(i, apps);
                    Assert.LessOrEqual(Mathf.Abs(cell.x) + AppLibrary.k_CellWidth / 2f, inner + 1e-3f, $"{apps} apps, cell {i}");
                    Assert.LessOrEqual(cell.y + AppLibrary.k_CellHeight / 2f, size.y / 2f - 16f + 1e-3f, $"{apps} apps, cell {i}");
                    Assert.Greater(cell.y - AppLibrary.k_CellHeight / 2f, AppLibrary.TaglinePosition(size).y, $"{apps} apps, cell {i}");
                }
            }
        }

        [Test]
        public void ThePhonesAppsAreLaidOutAsPagedFromTheStart()
        {
            // While the list loads, and with fewer apps than a page, the Android section already has room
            // for the arrows and the page row, so it keeps its size when the list comes in.
            foreach (var apps in new[] { 0, 4, 6, 7, 150 })
                Assert.AreEqual(AppLibrary.Size(7), AppLibrary.Size(AppLibrary.AndroidLayoutCount(apps)), $"{apps} apps");
            Assert.AreEqual(150, AppLibrary.AndroidLayoutCount(150));
            // Laid out that way, a phone's few apps sit where a full page's first ones would.
            Assert.AreEqual(AppLibrary.CellPosition(0, 150), AppLibrary.CellPosition(0, AppLibrary.AndroidLayoutCount(2)));
        }

        [Test]
        public void ManyPagesSayWhichPageInsteadOfDots()
        {
            Assert.IsTrue(AppLibrary.UsesPageDots(2));
            Assert.IsTrue(AppLibrary.UsesPageDots(AppLibrary.k_MaxPageDots));
            Assert.IsFalse(AppLibrary.UsesPageDots(AppLibrary.k_MaxPageDots + 1));
            Assert.AreEqual("3 of 25", AppLibrary.PageLabel(2, 25));
        }

        [Test]
        public void TheRailSitsBesideTheLibrary()
        {
            foreach (var withLandscape in new[] { false, true })
            {
                var rail = AppLibrary.RailSize(withLandscape);
                foreach (var apps in new[] { 6, 7 })
                {
                    var size = AppLibrary.Size(apps);
                    var position = AppLibrary.RailPosition(size, rail);
                    Assert.AreEqual(-size.x / 2f - AppLibrary.k_RailGap, position.x + rail.x / 2f, 1e-3f, $"{apps} apps");
                    Assert.AreEqual(0f, position.y);
                }
                // Its items inside it, top to bottom, none over another.
                var items = withLandscape ? 3 : 2;
                for (var i = 0; i < items; i++)
                {
                    var y = AppLibrary.RailItemY(i, withLandscape);
                    Assert.LessOrEqual(y + AppLibrary.k_RailItemHeight / 2f, rail.y / 2f + 1e-3f, $"item {i}");
                    Assert.GreaterOrEqual(y - AppLibrary.k_RailItemHeight / 2f, -rail.y / 2f - 1e-3f, $"item {i}");
                    if (i > 0)
                        Assert.LessOrEqual(y + AppLibrary.k_RailItemHeight / 2f,
                            AppLibrary.RailItemY(i - 1, withLandscape) - AppLibrary.k_RailItemHeight / 2f + 1e-3f, $"item {i}");
                }
            }
            // With the Landscape switch it's still no taller than the library, so it doesn't reach the panel below.
            Assert.LessOrEqual(AppLibrary.RailSize(true).y, AppLibrary.Size(7).y);
        }

        [Test]
        public void FewerRowsMakeMorePages()
        {
            Assert.AreEqual(3, AppLibrary.PerPage(1));
            Assert.AreEqual(2, AppLibrary.Pages(6, rows: 1));
            Assert.AreEqual(1, AppLibrary.PageOf(3, rows: 1));
            Assert.Less(AppLibrary.Size(6, rows: 1).y, AppLibrary.Size(6).y);
        }
    }
}
