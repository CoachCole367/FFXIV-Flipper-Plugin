using System.Text.Json;
namespace FFXIVFlipper;
public static class TableSorting
{
    public static int[] Opportunities(JsonElement[] rows, int column, bool descending, Func<JsonElement, bool> inBasket)
    {
        var order = Enumerable.Range(0, rows.Length).ToArray();
        Array.Sort(order, (a, b) =>
        {
            var left = rows[a]; var right = rows[b];
            int comparison;
            if (column is 0 or 1)
                comparison = StringComparer.OrdinalIgnoreCase.Compare(Text(left, column == 0 ? "itemName" : "buyWorld"), Text(right, column == 0 ? "itemName" : "buyWorld"));
            else if (column == 7) comparison = inBasket(left).CompareTo(inBasket(right));
            else
            {
                var field = column switch { 2 => "planCost", 3 => "targetSell", 5 => "sellProbAtRecommended", 6 => "daysToClearRecommended", _ => "planExpectedProfit" };
                var x = Number(left, field); var y = Number(right, field);
                if (x == null || y == null) return x == y ? a.CompareTo(b) : x == null ? 1 : -1;
                comparison = x.Value.CompareTo(y.Value);
            }
            if (descending) comparison = -comparison;
            return comparison != 0 ? comparison : a.CompareTo(b);
        });
        return order;
    }
    public static PurchaseLot[] Purchases(PurchaseLot[] rows, int column, bool descending, Func<PurchaseLot, string> itemName, Func<uint, string> worldName)
    {
        var result = rows.ToArray();
        Array.Sort(result, (a, b) =>
        {
            var comparison = column switch
            {
                1 => StringComparer.OrdinalIgnoreCase.Compare(itemName(a), itemName(b)),
                2 => StringComparer.OrdinalIgnoreCase.Compare(worldName(a.WorldId), worldName(b.WorldId)),
                3 => (a.Quantity, a.SoldQuantity).CompareTo((b.Quantity, b.SoldQuantity)),
                4 => a.TotalCost.CompareTo(b.TotalCost),
                5 => a.NetRevenue.CompareTo(b.NetRevenue),
                6 => a.RealizedProfit.CompareTo(b.RealizedProfit),
                _ => a.PurchasedAt.CompareTo(b.PurchasedAt)
            };
            if (descending) comparison = -comparison;
            return comparison != 0 ? comparison : a.Id.CompareTo(b.Id);
        });
        return result;
    }
    private static string Text(JsonElement row, string field) => row.TryGetProperty(field, out var value) ? value.ToString() : "";
    private static double? Number(JsonElement row, string field) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
}
