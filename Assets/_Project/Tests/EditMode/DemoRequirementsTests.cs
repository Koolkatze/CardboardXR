using CardboardXR.Demos;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    public class DemoRequirementsTests
    {
        static readonly CoreState k_ThreeDof = new(false, false, true);
        static readonly CoreState k_Passthrough = new(true, true, true);

        [Test]
        public void AnyLeavesEverythingAsItWas()
        {
            Assert.AreEqual(k_ThreeDof, CoreState.Resolve(k_ThreeDof, default));
            Assert.AreEqual(k_Passthrough, CoreState.Resolve(k_Passthrough, default));
        }

        [Test]
        public void PassthroughStaysInThreeDof()
        {
            var state = CoreState.Resolve(k_ThreeDof, new DemoRequirements { Passthrough = Need.On });
            Assert.AreEqual(new CoreState(false, true, true), state);
        }

        [Test]
        public void HandsTurnSixDofOn()
        {
            var state = CoreState.Resolve(new CoreState(false, false, false), new DemoRequirements { Hands = Need.On });
            Assert.AreEqual(new CoreState(true, false, true), state);
        }

        [Test]
        public void SixDofOffKeepsPassthrough()
        {
            var state = CoreState.Resolve(k_Passthrough, new DemoRequirements { SixDof = Need.Off });
            Assert.AreEqual(new CoreState(false, true, true), state);
        }

        [Test]
        public void SixDofOffWinsOverHandsOn()
        {
            var state = CoreState.Resolve(k_ThreeDof, new DemoRequirements { SixDof = Need.Off, Passthrough = Need.On, Hands = Need.On });
            Assert.AreEqual(new CoreState(false, true, true), state);
        }

        [Test]
        public void TheLobbyIsThreeDofPassthrough()
        {
            var catalog = UnityEngine.ScriptableObject.CreateInstance<DemoCatalog>();
            try
            {
                Assert.AreEqual(new CoreState(false, true, true), CoreState.Resolve(k_Passthrough, catalog.LobbyRequirements));
                Assert.AreEqual(new CoreState(true, true, true), CoreState.Resolve(k_ThreeDof, DemoCatalog.SixDofLobby));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(catalog);
            }
        }

        [Test]
        public void VideoLikeDemoTurnsHandsAndCameraOff()
        {
            var needs = new DemoRequirements { SixDof = Need.Off, Passthrough = Need.Off, Hands = Need.Off };
            Assert.AreEqual(new CoreState(false, false, false), CoreState.Resolve(k_Passthrough, needs));
        }

        [Test]
        public void PassthroughOffKeepsSixDof()
        {
            var state = CoreState.Resolve(k_Passthrough, new DemoRequirements { Passthrough = Need.Off });
            Assert.AreEqual(new CoreState(true, false, true), state);
        }

        [Test]
        public void CatalogFindsEntriesByIndexAndId()
        {
            var catalog = UnityEngine.ScriptableObject.CreateInstance<DemoCatalog>();
            try
            {
                Assert.IsFalse(catalog.TryGet(0, out _));
                Assert.IsFalse(catalog.TryGet(-1, out _));
                Assert.AreEqual(-1, catalog.IndexOf("sandbox"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(catalog);
            }
        }
    }
}
