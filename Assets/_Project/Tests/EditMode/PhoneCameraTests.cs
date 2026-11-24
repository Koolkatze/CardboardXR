using System.Collections.Generic;
using CardboardXR.Hands;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class PhoneCameraTests
    {
        [Test]
        public void Lenses_ParseFromWidestToNarrowest()
        {
            var lenses = PhoneCamera.ParseLenses("0;1.0000;1.0000;0.60000;0.45000\n" +
                                                 "0;5.0000;5.0000;0.12000;0.09000\n" +
                                                 "0;0.5000;0.5000;1.20000;0.90000\n" +
                                                 "broken line\n" +
                                                 "2;1.0000;0.6000;1.00000;0.75000\n");
            Assert.AreEqual(4, lenses.Count);
            Assert.AreEqual("0.5x", lenses[0].Label);
            Assert.AreEqual("0.6x", lenses[1].Label);
            Assert.AreEqual("2", lenses[1].CameraId);
            Assert.AreEqual("1x", lenses[2].Label);
            Assert.AreEqual("5x", lenses[3].Label);
            Assert.AreEqual(5f, lenses[3].ZoomRatio, 1e-5f);
            Assert.AreEqual(0.12f, lenses[3].Tangents.x, 1e-5f);
            Assert.AreEqual(0, PhoneCamera.ParseLenses("").Count);
        }

        [Test]
        public void Lenses_AreLabelledAsTheCameraAppDoes()
        {
            Assert.AreEqual("0.5x", PhoneCamera.LensLabel(0.49f));
            Assert.AreEqual("0.6x", PhoneCamera.LensLabel(0.6f));
            Assert.AreEqual("1x", PhoneCamera.LensLabel(1f));
            Assert.AreEqual("1.3x", PhoneCamera.LensLabel(1.3f));
            Assert.AreEqual("2x", PhoneCamera.LensLabel(2.04f));
            Assert.AreEqual("2.5x", PhoneCamera.LensLabel(2.5f));
            Assert.AreEqual("5x", PhoneCamera.LensLabel(4.93f));
            Assert.AreEqual("10x", PhoneCamera.LensLabel(10.1f));
        }

        [Test]
        public void ImageFromScreen_StandsThePictureUpright()
        {
            // A picture whose top-left pixel is P: in the planes (s right, t from the top) it's at (0, 0).
            // Turned upright by each rotation, P must land where that corner goes: unturned, still the
            // top-left (uv 0, 1); turned 90 clockwise, the top-right (1, 1); 180, the bottom-right
            // (1, 0); 270, the bottom-left (0, 0).
            var cornerAfter = new Dictionary<int, Vector2>
            {
                [0] = new(0f, 1f), [90] = new(1f, 1f), [180] = new(1f, 0f), [270] = new(0f, 0f),
            };
            foreach (var pair in cornerAfter)
            {
                PhoneCamera.ImageFromScreen(pair.Key, out var rowX, out var rowY);
                var st = Sample(rowX, rowY, pair.Value);
                Assert.That((st - Vector2.zero).magnitude, Is.LessThan(1e-5f), $"turned {pair.Key}: {st}");
                // And the picture's top-right (1, 0) goes a quarter turn on from there.
                var next = cornerAfter[(pair.Key + 90) % 360];
                Assert.That((Sample(rowX, rowY, next) - new Vector2(1f, 0f)).magnitude, Is.LessThan(1e-5f), $"turned {pair.Key}");
            }
        }

        [Test]
        public void UprightTangents_SwapForAQuarterTurn()
        {
            var stream = new Vector2(0.6f, 0.34f);
            Assert.AreEqual(stream, PhoneCamera.UprightTangents(stream, 0));
            Assert.AreEqual(stream, PhoneCamera.UprightTangents(stream, 180));
            Assert.AreEqual(new Vector2(0.34f, 0.6f), PhoneCamera.UprightTangents(stream, 90));
            Assert.AreEqual(new Vector2(0.34f, 0.6f), PhoneCamera.UprightTangents(stream, 270));
        }

        [Test]
        public void ImageRay_GoesThroughTheFieldOfView()
        {
            var tangents = new Vector2(0.6f, 0.34f);
            Assert.That((PhoneCamera.ImageRay(new Vector2(0.5f, 0.5f), tangents) - Vector3.forward).magnitude, Is.LessThan(1e-5f));
            // The top-right corner: right and up by the tangents.
            var corner = PhoneCamera.ImageRay(new Vector2(1f, 0f), tangents);
            Assert.AreEqual(0.6f, corner.x / corner.z, 1e-5f);
            Assert.AreEqual(0.34f, corner.y / corner.z, 1e-5f);
            Assert.AreEqual(1f, corner.magnitude, 1e-5f);
        }

        [Test]
        public void PalmDecoder_FindsTwoSeparatePalms()
        {
            var anchors = PalmDecoder.CreateAnchors();
            var scores = new float[PalmDecoder.AnchorCount];
            var boxes = new float[PalmDecoder.AnchorCount * PalmDecoder.ValuesPerAnchor];
            for (var i = 0; i < scores.Length; i++)
                scores[i] = -10f;
            // One palm seen by two neighbouring anchors, another far off, weaker.
            var first = AnchorNear(anchors, new Vector2(0.3f, 0.5f));
            var echo = first + 1;
            var second = AnchorNear(anchors, new Vector2(0.75f, 0.5f));
            foreach (var (i, score) in new[] { (first, 4f), (echo, 3f), (second, 2f) })
            {
                scores[i] = score;
                boxes[i * PalmDecoder.ValuesPerAnchor + 2] = 0.2f * PalmDecoder.InputSize;
                boxes[i * PalmDecoder.ValuesPerAnchor + 3] = 0.2f * PalmDecoder.InputSize;
            }
            var palms = new List<Palm>();
            PalmDecoder.DecodeTop(boxes, scores, anchors, 0.5f, 2, palms);
            Assert.AreEqual(2, palms.Count);
            Assert.That((palms[0].center - anchors[first]).magnitude, Is.LessThan(1e-5f));
            Assert.That((palms[1].center - anchors[second]).magnitude, Is.LessThan(1e-5f));
            // Scores are logits: 4 is 0.982, 2 is 0.881.
            PalmDecoder.DecodeTop(boxes, scores, anchors, 0.98f, 2, palms);
            Assert.AreEqual(1, palms.Count, "only the first reaches 0.98");
        }

        [Test]
        public void TwoRegionsOnOneHandAreTheSameHand()
        {
            var a = new HandRegion { center = new Vector2(100f, 100f), size = 80f };
            Assert.IsTrue(PinchSensor.SameHand(a, new HandRegion { center = new Vector2(120f, 110f), size = 90f }));
            Assert.IsFalse(PinchSensor.SameHand(a, new HandRegion { center = new Vector2(260f, 100f), size = 80f }));
        }

        static Vector2 Sample(Vector4 rowX, Vector4 rowY, Vector2 uv) =>
            new(rowX.x * uv.x + rowX.y * uv.y + rowX.z, rowY.x * uv.x + rowY.y * uv.y + rowY.z);

        static int AnchorNear(Vector2[] anchors, Vector2 point)
        {
            var best = 0;
            for (var i = 1; i < anchors.Length; i++)
            {
                if ((anchors[i] - point).sqrMagnitude < (anchors[best] - point).sqrMagnitude)
                    best = i;
            }
            return best;
        }
    }
}
