using CardboardXR.Hands;
using NUnit.Framework;

namespace CardboardXR.Stereo.Tests
{
    public class PinchRuleTests
    {
        const float k_Open = 1.9f;
        const float k_Pinched = 0.23f;

        static PinchRule Steady()
        {
            var rule = new PinchRule();
            for (var i = 0; i < PinchSensor.k_SteadyResults; i++)
                rule.Update(true, k_Open, 0f);
            return rule;
        }

        static PinchRule Pinched()
        {
            var rule = Steady();
            Assert.AreEqual(PinchRule.Change.Closed, rule.Update(true, k_Pinched, 0f));
            return rule;
        }

        [Test]
        public void ClosingFingertipsOnASteadyHandPinches()
        {
            var rule = Pinched();
            Assert.IsTrue(rule.IsPinched);
        }

        [Test]
        public void AHandJustArrivedDoesNotPinchYet()
        {
            var rule = new PinchRule();
            for (var i = 0; i < PinchSensor.k_SteadyResults; i++)
                Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched, 0f));
            // Held pinched long enough, it counts.
            Assert.AreEqual(PinchRule.Change.Closed, rule.Update(true, k_Pinched, 0f));
        }

        [Test]
        public void OnePinchClicksOnceUntilTheFingersOpen()
        {
            var rule = Pinched();
            // In between the thresholds it stays pinched: no second click from jitter.
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, PinchSensor.k_CloseRatio + 0.1f, 0f));
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched, 0f));
            // Open in k_OpenResults results in a row, it opens.
            for (var i = 1; i < PinchSensor.k_OpenResults; i++)
                Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Open, 0f));
            Assert.AreEqual(PinchRule.Change.Opened, rule.Update(true, k_Open, 0f));
            Assert.AreEqual(PinchRule.Change.Closed, rule.Update(true, k_Pinched, 0f));
        }

        [Test]
        public void OneMisreadDoesNotLetGoOfAHeldPinch()
        {
            var rule = Pinched();
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Open, 0.1f));
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched, 0.2f));
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Open, 0.3f));
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched, 0.4f));
            Assert.IsTrue(rule.IsPinched);
        }

        [Test]
        public void AHeldPinchOutlastsTheHandGoingUnseenAMoment()
        {
            var rule = Pinched();
            Assert.AreEqual(PinchRule.Change.None, rule.Update(false, 0f, 1f));
            Assert.IsTrue(rule.IsHeldWhileLost);
            Assert.AreEqual(PinchRule.Change.None, rule.Update(false, 0f, 1f + PinchSensor.k_LostGraceSeconds * 0.5f));
            // Seen again, still pinched: the hold carries on.
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched, 1.3f));
            Assert.IsTrue(rule.IsPinched);
            Assert.IsFalse(rule.IsHeldWhileLost);
        }

        [Test]
        public void LosingTheHandForLongerOpensAndStartsTheWaitAgain()
        {
            var rule = Pinched();
            Assert.AreEqual(PinchRule.Change.None, rule.Update(false, 0f, 1f));
            Assert.AreEqual(PinchRule.Change.Opened, rule.Update(false, 0f, 1.01f + PinchSensor.k_LostGraceSeconds));
            Assert.IsFalse(rule.IsPinched);
            Assert.AreEqual(PinchRule.Change.None, rule.Update(true, k_Pinched, 2f));
        }
    }
}
