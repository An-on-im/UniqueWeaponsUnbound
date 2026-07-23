using RimWorld;
using Xunit;

namespace UniqueWeaponsUnbound.Tests
{
    /// <summary>
    /// Tests for TraitCostUtility.IsNegativeTrait, which drives the inverted
    /// cost/refund logic (cheap to add, costs to remove) for undesirable
    /// traits. Covers both detection signals: a MarketValue statFactor below
    /// 1.0, and WeaponTraitDef's own marketValueOffset below 0.
    /// </summary>
    public class TraitCostUtilityTests
    {
        [Fact]
        public void NegativeMarketValueOffset_IsNegativeTrait()
        {
            var trait = new WeaponTraitDef { defName = "TestNegativeOffset", marketValueOffset = -50f };

            Assert.True(TraitCostUtility.IsNegativeTrait(trait));
        }

        [Fact]
        public void PositiveMarketValueOffset_IsNotNegativeTrait()
        {
            var trait = new WeaponTraitDef { defName = "TestPositiveOffset", marketValueOffset = 50f };

            Assert.False(TraitCostUtility.IsNegativeTrait(trait));
        }

        [Fact]
        public void ZeroMarketValueOffsetAndNoStatFactors_IsNotNegativeTrait()
        {
            var trait = new WeaponTraitDef { defName = "TestNeutralTrait" };

            Assert.False(TraitCostUtility.IsNegativeTrait(trait));
        }
    }
}
