using System.IO;
using System.Xml;
using UnityEditor.Android;

namespace ProjectBootstrap
{
    // What the app library's Android section (AndroidApps, CardboardXRApps.java) needs in the manifest:
    //   - A <queries> entry for launcher activities: from Android 11 an app sees only the other apps
    //     its manifest says it looks for, so without it the section would be empty.
    //   - SYSTEM_ALERT_WINDOW, "Display over other apps", for the overlay that keeps an opened app in
    //     landscape. Declaring it doesn't grant it: the user turns it on in Settings, which the app
    //     opens the first time it's needed.
    //   - POST_NOTIFICATIONS, for the notification that ends the landscape hold. On Android 13 and
    //     later the user is asked for it, once, along with the overlay.
    // And what opening the apps on a screen in XR needs (AppScreen, CardboardXRScreen.java): Shizuku's
    // API library in the module's dependencies, its provider (through which the Shizuku app hands
    // CardboardXR its binder) and a <queries> entry for the Shizuku app, so CardboardXR can see it.
    class AndroidAppsManifest : IPostGenerateGradleAndroidProject
    {
        const string k_AndroidNamespace = "http://schemas.android.com/apk/res/android";
        static readonly string[] k_Permissions =
        {
            "android.permission.SYSTEM_ALERT_WINDOW",
            "android.permission.POST_NOTIFICATIONS",
        };

        // Shizuku's API on Maven Central (https://github.com/RikkaApps/Shizuku-API).
        const string k_ShizukuVersion = "13.1.5";
        const string k_ShizukuPackage = "moe.shizuku.privileged.api";

        public int callbackOrder => 0;

        // path is the generated unityLibrary module.
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            var document = new XmlDocument();
            document.Load(manifestPath);
            var manifest = document.DocumentElement;
            var changed = false;
            foreach (var name in k_Permissions)
                changed |= AddPermission(document, manifest, name);
            changed |= AddLauncherQuery(document, manifest);
            changed |= AddShizukuQuery(document, manifest);
            changed |= AddShizukuProvider(document, manifest);
            if (changed)
                document.Save(manifestPath);
            AddShizukuDependencies(Path.Combine(path, "build.gradle"));
        }

        // A second dependencies block at the end, which Gradle adds to the first, so it holds whatever
        // shape Unity gives the generated file.
        static void AddShizukuDependencies(string gradlePath)
        {
            var gradle = File.ReadAllText(gradlePath);
            if (gradle.Contains("dev.rikka.shizuku:api"))
                return;
            File.AppendAllText(gradlePath,
                "\n// Added by AndroidAppsManifest: Shizuku, for the phone's apps on a screen in XR.\n" +
                "dependencies {\n" +
                $"    implementation 'dev.rikka.shizuku:api:{k_ShizukuVersion}'\n" +
                $"    implementation 'dev.rikka.shizuku:provider:{k_ShizukuVersion}'\n" +
                "}\n");
        }

        static bool AddShizukuQuery(XmlDocument document, XmlElement manifest)
        {
            foreach (XmlElement package in manifest.GetElementsByTagName("package"))
            {
                if (package.GetAttribute("name", k_AndroidNamespace) == k_ShizukuPackage)
                    return false;
            }
            var queries = document.CreateElement("queries");
            var element = document.CreateElement("package");
            element.SetAttribute("name", k_AndroidNamespace, k_ShizukuPackage);
            queries.AppendChild(element);
            manifest.AppendChild(queries);
            return true;
        }

        // As Shizuku's README has it; the permission keeps other apps from calling it.
        static bool AddShizukuProvider(XmlDocument document, XmlElement manifest)
        {
            if (manifest["application"] is not XmlElement application)
                return false;
            foreach (XmlElement provider in application.GetElementsByTagName("provider"))
            {
                if (provider.GetAttribute("name", k_AndroidNamespace) == "rikka.shizuku.ShizukuProvider")
                    return false;
            }
            var element = document.CreateElement("provider");
            element.SetAttribute("name", k_AndroidNamespace, "rikka.shizuku.ShizukuProvider");
            element.SetAttribute("authorities", k_AndroidNamespace, "${applicationId}.shizuku");
            element.SetAttribute("multiprocess", k_AndroidNamespace, "false");
            element.SetAttribute("enabled", k_AndroidNamespace, "true");
            element.SetAttribute("exported", k_AndroidNamespace, "true");
            element.SetAttribute("permission", k_AndroidNamespace, "android.permission.INTERACT_ACROSS_USERS_FULL");
            application.AppendChild(element);
            return true;
        }

        static bool AddPermission(XmlDocument document, XmlElement manifest, string name)
        {
            foreach (XmlElement permission in manifest.GetElementsByTagName("uses-permission"))
            {
                if (permission.GetAttribute("name", k_AndroidNamespace) == name)
                    return false;
            }
            var element = document.CreateElement("uses-permission");
            element.SetAttribute("name", k_AndroidNamespace, name);
            manifest.PrependChild(element);
            return true;
        }

        // <queries><intent><action MAIN/><category LAUNCHER/></intent></queries>, a child of <manifest>.
        static bool AddLauncherQuery(XmlDocument document, XmlElement manifest)
        {
            foreach (XmlElement category in manifest.GetElementsByTagName("category"))
            {
                if (category.GetAttribute("name", k_AndroidNamespace) == "android.intent.category.LAUNCHER" &&
                    category.ParentNode?.ParentNode is XmlElement { Name: "queries" })
                    return false;
            }
            var queries = document.CreateElement("queries");
            var intent = document.CreateElement("intent");
            var action = document.CreateElement("action");
            action.SetAttribute("name", k_AndroidNamespace, "android.intent.action.MAIN");
            var launcher = document.CreateElement("category");
            launcher.SetAttribute("name", k_AndroidNamespace, "android.intent.category.LAUNCHER");
            intent.AppendChild(action);
            intent.AppendChild(launcher);
            queries.AppendChild(intent);
            manifest.AppendChild(queries);
            return true;
        }
    }
}
