using System.Globalization;
using System.Windows.Data;

namespace TakaneAttendance.Wpf;

/// <summary>値があれば true。一覧で行を選んでいるときだけボタンを押せるようにするために使う。</summary>
public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true/false を反転する。「自動判定」チェック時に年月コンボを無効化するために使う。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
