namespace UniqueWeaponsUnbound
{
    // Controls what happens to the weapon after customization completes or is
    // interrupted. Derived at runtime in the job driver's acquire-weapon toil
    // based on the weapon's location at job start (equipped, inventory, or
    // map). LeaveOnWorkbench is the zero-value default so uninitialized fields
    // produce the safest behavior.
    public enum WeaponReturnMode
    {
        LeaveOnWorkbench,
        Reequip,
        ReturnToInventory,
    }
}
