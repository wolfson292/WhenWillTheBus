// SPDX-License-Identifier: GPL-3.0-or-later

using ObjCRuntime;
using UIKit;

namespace WhenWillTheBus.App;

public static class Program
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
