using FFXIVFlipper;
static void Equal(decimal expected, decimal actual) { if (expected != actual) throw new Exception($"Expected {expected}, got {actual}"); }
Equal(-810, StackEconomics.CashGain(100, 10, 200, 1));
Equal(900, StackEconomics.CashGain(100, 10, 200, 10));
Equal(-1000, StackEconomics.CashGain(100, 10, 200, 0));
Equal(4500000000m, StackEconomics.CashGain(100000000, 100, 200000000, 100, .275m));
try { StackEconomics.CashGain(100, 10, 200, 11); throw new Exception("Overselling accepted"); } catch (ArgumentOutOfRangeException) { }
Console.WriteLine("5 economics regressions passed.");
