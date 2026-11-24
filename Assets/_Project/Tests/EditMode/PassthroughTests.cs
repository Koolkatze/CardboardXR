using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class PassthroughTests
    {
        [Test]
        public void CroppedDisplayTransform_WidensToTheWholeImage()
        {
            // A 4:3 image on a wider screen: all of its width, the middle 60% of its height.
            var display = Transform(new Vector2(1f, 0f), new Vector2(0f, 0.6f), new Vector2(0f, 0.2f));
            Assert.IsTrue(PassthroughController.TryWidenDisplayTransform(display, out var widened, out var min, out var max));

            AssertClose(new Vector2(0f, -1f / 3f), min);
            AssertClose(new Vector2(1f, 4f / 3f), max);
            AssertClose(new Vector2(0f, 0f), Apply(widened, new Vector2(0f, 0f)));
            AssertClose(new Vector2(1f, 1f), Apply(widened, new Vector2(1f, 1f)));
        }

        [Test]
        public void RotatedDisplayTransform_StillCoversEveryCorner()
        {
            // The image turned a quarter turn and cropped: tex = (0.2 + 0.6 v, 1 - u).
            var display = Transform(new Vector2(0f, -1f), new Vector2(0.6f, 0f), new Vector2(0.2f, 1f));
            Assert.IsTrue(PassthroughController.TryWidenDisplayTransform(display, out var widened, out _, out _));

            var corners = new HashSet<Vector2Int>();
            foreach (var screen in new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one })
            {
                var texture = Apply(widened, screen);
                var corner = new Vector2Int(Mathf.RoundToInt(texture.x), Mathf.RoundToInt(texture.y));
                AssertClose(corner, texture);
                corners.Add(corner);
            }
            Assert.AreEqual(4, corners.Count, "each screen corner lands on a different image corner");
        }

        [Test]
        public void FillZoom_EnlargesTheImageUntilItCoversTheView()
        {
            // A camera 1.2 x 0.9 across at a meter (about 62 x 48 degrees) in a view reaching 0.9 to
            // each side and 0.8 up and down: the height is what's short, by 0.8 / 0.45.
            var size = new Vector2(1.2f, 0.9f);
            var view = new Vector4(0.9f, 0.9f, 0.8f, 0.8f);
            var fill = PassthroughController.FillZoom(size, Vector2.zero, view);

            Assert.AreEqual(0.8f / 0.45f, fill, 1e-4f);
            AssertCovers(size, Vector2.zero, fill, view);
        }

        [Test]
        public void FillZoom_AllowsForAnOffCentreCamera()
        {
            // The image's middle a little right of straight ahead: enlarged about straight ahead, its
            // left edge, nearer, is the one short.
            var size = new Vector2(1.2f, 0.9f);
            var center = new Vector2(0.1f, 0f);
            var view = new Vector4(0.9f, 0.9f, 0.4f, 0.4f);
            var fill = PassthroughController.FillZoom(size, center, view);

            Assert.AreEqual(0.9f / (0.6f - 0.1f), fill, 1e-4f);
            AssertCovers(size, center, fill, view);
        }

        [Test]
        public void ZoomWindow_KeepsStraightAheadWhereItIs()
        {
            // Straight ahead stays under the reticle at every zoom, and everything else is enlarged
            // about it: a point at tangent t is shown at fill x zoom x t.
            var size = new Vector2(1.2f, 0.9f);
            var center = new Vector2(0.03f, -0.02f);
            const float fill = 1.7f;
            foreach (var zoom in PassthroughController.k_ZoomSteps)
            {
                PassthroughController.ZoomWindow(size, center, zoom, out var lo, out var hi);
                PassthroughController.ShownRect(size, center, lo, hi, fill * zoom, out var shownCenter, out var shownSize);
                Assert.AreEqual(1f / zoom, hi.x - lo.x, 1e-5f);
                Assert.AreEqual(1f / zoom, hi.y - lo.y, 1e-5f);
                AssertClose(size * fill, shownSize);
                foreach (var t in new[] { Vector2.zero, new Vector2(0.04f, 0.03f), new Vector2(-0.05f, 0.02f) })
                {
                    // Where t is in the image, then in the window drawn, then on the screen.
                    var uv = new Vector2(0.5f + (t.x - center.x) / size.x, 0.5f + (t.y - center.y) / size.y);
                    var inWindow = new Vector2((uv.x - lo.x) / (hi.x - lo.x), (uv.y - lo.y) / (hi.y - lo.y));
                    var shown = new Vector2(shownCenter.x + (inWindow.x - 0.5f) * shownSize.x, shownCenter.y + (inWindow.y - 0.5f) * shownSize.y);
                    AssertClose(t * (fill * zoom), shown);
                }
            }
        }

        [Test]
        public void ZoomWindow_StaysInsideTheImage()
        {
            // Straight ahead near the image's left edge: the window slides right rather than past it.
            PassthroughController.ZoomWindow(new Vector2(1.2f, 0.9f), new Vector2(0.55f, 0f), 2f, out var lo, out var hi);
            AssertClose(new Vector2(0f, 0.25f), lo);
            AssertClose(new Vector2(0.5f, 0.75f), hi);
            PassthroughController.ZoomWindow(new Vector2(1.2f, 0.9f), new Vector2(0.55f, 0f), 1f, out lo, out hi);
            AssertClose(Vector2.zero, lo);
            AssertClose(Vector2.one, hi);
        }

        [Test]
        public void FillZoom_NeverShrinksAndHasALimit()
        {
            var view = new Vector4(0.9f, 0.9f, 0.8f, 0.8f);
            Assert.AreEqual(1f, PassthroughController.FillZoom(new Vector2(3f, 3f), Vector2.zero, view), 1e-6f,
                "a camera that already covers the view stays at true scale");
            Assert.AreEqual(PassthroughController.k_MaxFill, PassthroughController.FillZoom(new Vector2(0.1f, 0.1f), Vector2.zero, view), 1e-6f);
            Assert.AreEqual(1f, PassthroughController.FillZoom(Vector2.zero, Vector2.zero, view), 1e-6f, "no frustum yet");
        }

        [Test]
        public void CroppedDisplayTransform_ShowsTheMiddleOfTheImage()
        {
            // The rotated, widened transform of the test above, zoomed 3x: the screen's corners land on
            // the middle third of the image, and its middle stays the image's middle.
            var display = Transform(new Vector2(0f, -1f), new Vector2(0.6f, 0f), new Vector2(0.2f, 1f));
            Assert.IsTrue(PassthroughController.TryWidenDisplayTransform(display, out var widened, out _, out _));
            const float zoom = 3f;
            var lo = Vector2.one * (0.5f - 0.5f / zoom);
            var hi = Vector2.one * (0.5f + 0.5f / zoom);
            var cropped = PassthroughController.CropDisplayTransform(widened, lo, hi);

            AssertClose(Apply(widened, lo), Apply(cropped, Vector2.zero));
            AssertClose(Apply(widened, hi), Apply(cropped, Vector2.one));
            AssertClose(Apply(widened, new Vector2(lo.x, hi.y)), Apply(cropped, Vector2.up));
            AssertClose(new Vector2(0.5f, 0.5f), Apply(cropped, new Vector2(0.5f, 0.5f)));
            foreach (var corner in new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one })
            {
                var texture = Apply(cropped, corner);
                Assert.AreEqual(1f / zoom / 2f, Mathf.Abs(texture.x - 0.5f), 1e-5f);
                Assert.AreEqual(1f / zoom / 2f, Mathf.Abs(texture.y - 0.5f), 1e-5f);
            }
        }

        // The image's edges, enlarged by fill about straight ahead, reach at least the view's edges.
        static void AssertCovers(Vector2 size, Vector2 center, float fill, Vector4 view)
        {
            var half = size / 2f;
            Assert.LessOrEqual((center.x - half.x) * fill, -view.x + 1e-5f, "left");
            Assert.GreaterOrEqual((center.x + half.x) * fill, view.y - 1e-5f, "right");
            Assert.LessOrEqual((center.y - half.y) * fill, -view.z + 1e-5f, "bottom");
            Assert.GreaterOrEqual((center.y + half.y) * fill, view.w - 1e-5f, "top");
        }

        // The ARCore shader's convention: tex = u * (m00, m01) + v * (m10, m11) + (m20, m21).
        static Matrix4x4 Transform(Vector2 perU, Vector2 perV, Vector2 offset)
        {
            var matrix = Matrix4x4.identity;
            matrix.m00 = perU.x;
            matrix.m01 = perU.y;
            matrix.m10 = perV.x;
            matrix.m11 = perV.y;
            matrix.m20 = offset.x;
            matrix.m21 = offset.y;
            return matrix;
        }

        static Vector2 Apply(Matrix4x4 m, Vector2 uv) =>
            new(uv.x * m.m00 + uv.y * m.m10 + m.m20, uv.x * m.m01 + uv.y * m.m11 + m.m21);

        static void AssertClose(Vector2 expected, Vector2 actual) =>
            Assert.That((actual - expected).magnitude, Is.LessThan(1e-5f), $"expected {expected}, got {actual}");
    }
}
