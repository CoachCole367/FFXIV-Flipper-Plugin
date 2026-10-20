namespace FFXIVFlipper;
public static class StackEconomics
{
    // All units in the purchased stack are paid for, including unsold leftovers.
    public static decimal CashGain(int unitCost, int stackSize, int sellPrice, int soldUnits, decimal taxRate = .05m)
    {
        if (unitCost < 0 || stackSize <= 0 || sellPrice < 0 || soldUnits < 0 || soldUnits > stackSize || taxRate < 0 || taxRate > 1)
            throw new ArgumentOutOfRangeException(nameof(stackSize));
        return sellPrice * (decimal)soldUnits * (1 - taxRate) - unitCost * (decimal)stackSize;
    }
}
