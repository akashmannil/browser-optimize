using System.IO;
using System.Windows;

namespace Hearth;

public partial class App : Application
{
    /// <summary>
    /// Root folder for everything Hearth persists: the shared WebView2 user-data
    /// folder, hibernation screenshots, and the session index.
    /// Kept beside the executable so a portfolio checkout stays self-contained.
    /// </summary>
    public static string StoreRoot { get; } = InitStoreRoot();

    private static string InitStoreRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "store");
        Directory.CreateDirectory(root);
        return root;
    }
}
