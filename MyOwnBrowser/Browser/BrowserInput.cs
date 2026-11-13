using CefSharp.Enums;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace MyOwnBrowser.Browser;

public sealed class BrowserInput : Control
{
    internal void UpdateCursor(CursorType type)
    {
        InputSystemCursorShape shape = type switch
        {
            CursorType.Hand => InputSystemCursorShape.Hand,
            CursorType.IBeam => InputSystemCursorShape.IBeam,
            CursorType.Cross => InputSystemCursorShape.Cross,
            CursorType.Wait => InputSystemCursorShape.Wait,
            CursorType.NorthSouthResize => InputSystemCursorShape.SizeNorthSouth,
            CursorType.EastWestResize => InputSystemCursorShape.SizeWestEast,
            _ => InputSystemCursorShape.Arrow
        };
        ProtectedCursor = InputSystemCursor.Create(shape);
    }
}
