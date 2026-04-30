using System.Collections.Specialized;

namespace Core;

public sealed class FocusSpellInRange : IReader
{
    private const int focusCell = 133;

    public bool this[int index] => b[index];

    private BitVector32 b;

    public FocusSpellInRange() { }

    public void Update(IAddonDataProvider reader)
    {
        b = new(reader.GetInt(focusCell));
    }

    // Warrior
    public bool Warrior_Charge => b[Mask._0];
    public bool Warrior_Rend => b[Mask._1];
    public bool Warrior_ShootGun => b[Mask._2];
    public bool Warrior_Throw => b[Mask._3];

    // Rogue
    public bool Rogue_SinisterStrike => b[Mask._0];
    public bool Rogue_Throw => b[Mask._1];
    public bool Rogue_ShootGun => b[Mask._2];

    // Priest
    public bool Priest_ShadowWordPain => b[Mask._0];
    public bool Priest_Shoot => b[Mask._1];
    public bool Priest_MindFlay => b[Mask._2];
    public bool Priest_MindBlast => b[Mask._3];
    public bool Priest_Smite => b[Mask._4];
    public bool Priest_Divine_Spirit => b[Mask._5];
    public bool Priest_Power_World_Fortitude => b[Mask._6];
    public bool Priest_Power_Word_Shield => b[Mask._7];
    public bool Priest_Lesser_Heal => b[Mask._8];
    public bool Priest_Prayer_of_Mending => b[Mask._9];
    public bool Priest_Renew => b[Mask._10];
    public bool Priest_Shadow_Protection => b[Mask._11];

    // Druid
    public bool Druid_Wrath => b[Mask._0];
    public bool Druid_Bash => b[Mask._1];
    public bool Druid_Rip => b[Mask._2];
    public bool Druid_Maul => b[Mask._3];
    public bool Druid_Healing_Touch => b[Mask._4];
    public bool Druid_Mark_of_the_Wild => b[Mask._5];
    public bool Druid_Regrowth => b[Mask._6];
    public bool Druid_Rejuvenation => b[Mask._7];
    public bool Druid_Thorns => b[Mask._8];

    //Paladin
    public bool Paladin_Judgement => b[Mask._0];
    public bool Paladin_Exorcism => b[Mask._1];
    public bool Paladin_Flash_Heal => b[Mask._2];
    public bool Paladin_Holy_Light => b[Mask._3];
    public bool Paladin_Blessing_of => b[Mask._4];
    public bool Paladin_Greater_Blessing_of => b[Mask._5];

    //Mage
    public bool Mage_Fireball => b[Mask._0];
    public bool Mage_Shoot => b[Mask._1];
    public bool Mage_Pyroblast => b[Mask._2];
    public bool Mage_Frostbolt => b[Mask._3];
    public bool Mage_Fireblast => b[Mask._4];

    //Hunter
    public bool Hunter_RaptorStrike => b[Mask._0];
    public bool Hunter_AutoShoot => b[Mask._1];
    public bool Hunter_SerpentSting => b[Mask._2];
    public bool Hunter_FeedPet => b[Mask._3];

    // Warlock
    public bool Warlock_ShadowBolt => b[Mask._0];
    public bool Warlock_Shoot => b[Mask._1];
    public bool Warlock_HealthFunnel => b[Mask._2];

    // Shaman
    public bool Shaman_LightningBolt => b[Mask._0];
    public bool Shaman_EarthShock => b[Mask._1];
    public bool Shaman_Healing_Wave => b[Mask._2];
    public bool Shaman_Lesser_Healing_Wave => b[Mask._3];
    public bool Shaman_Water_Breathing => b[Mask._4];
    public bool Shaman_Chain_Heal => b[Mask._5];
    public bool Shaman_Earth_Shield => b[Mask._6];

    // Death Knight
    public bool DeathKnight_IcyTouch => b[Mask._0];
    public bool DeathKnight_DeathCoil => b[Mask._1];
    public bool DeathKnight_DeathGrip => b[Mask._2];
    public bool DeathKnight_DarkCommand => b[Mask._3];
    public bool DeathKnight_RaiseDead => b[Mask._4];

}