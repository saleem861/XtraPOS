using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Sales;

public static class CheckoutFailure
{
    public static string Describe(SqlException exception) => Describe(exception.Number);

    public static string Describe(int number)
    {
        if (number is 207 or 208)
        {
            return "تعذر إتمام البيع. جداول البيع على هذا الجهاز تحتاج التحديث. أعد تشغيل الخدمة ثم أعد المحاولة.";
        }

        return "تعذر إتمام البيع (" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").";
    }

    public const string StockMessage = "تعذر إتمام البيع. تعذر تحديث مخزون الصنف.";
}
