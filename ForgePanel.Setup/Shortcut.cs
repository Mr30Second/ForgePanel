using System;
using System.IO;
using System.Reflection;

namespace ForgePanel.Setup
{
    // .lnk в обход Shell.Application: WScript.Shell доступен всегда, а net472 не даёт
    // динамической привязки к COM (dynamic падает на __ComObject — проверено на этой машине),
    // поэтому свойства ставятся через InvokeMember.
    // Save() вызывается БЕЗ аргументов: с массивом параметров бросает TargetParameterCountException.
    static class Shortcut
    {
        private static readonly BindingFlags Set = BindingFlags.SetProperty;
        private static readonly BindingFlags Call = BindingFlags.InvokeMethod;

        public static void Create(string linkFile, string target, string workingDirectory,
            string arguments, string description, string icon)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("Не найден WScript.Shell — ярлыки создать нечем.");

            object shell = Activator.CreateInstance(shellType);
            object link = shellType.InvokeMember("CreateShortcut", Call, null, shell,
                new object[] { Path.GetFullPath(linkFile) });
            Type linkType = link.GetType();

            Put(linkType, link, "TargetPath", Path.GetFullPath(target));
            if (workingDirectory != null) Put(linkType, link, "WorkingDirectory", Path.GetFullPath(workingDirectory));
            Put(linkType, link, "Arguments", arguments ?? "");
            Put(linkType, link, "Description", description ?? Product.Name);
            if (icon != null) Put(linkType, link, "IconLocation", icon + ",0");
            linkType.InvokeMember("WindowStyle", Set, null, link, new object[] { 1 });

            linkType.InvokeMember("Save", Call, null, link, new object[0]);
        }

        private static void Put(Type type, object instance, string property, object value)
        {
            if (value == null) return;
            type.InvokeMember(property, Set, null, instance, new object[] { value });
        }

        public static void Remove(string linkFile)
        {
            if (File.Exists(linkFile)) File.Delete(linkFile);
        }
    }
}
