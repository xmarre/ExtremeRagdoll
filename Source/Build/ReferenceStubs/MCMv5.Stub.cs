using System;
using System.Reflection;

[assembly: AssemblyVersion("5.12.1.0")]

namespace MCM.Abstractions.Base.Global
{
    public abstract class GlobalSettings<T>
    {
        public static T Instance { get { return default(T); } }
    }

    public abstract class AttributeGlobalSettings<T> : GlobalSettings<T>
    {
        public abstract string Id { get; }
        public abstract string DisplayName { get; }
        public abstract string FolderName { get; }
        public abstract string FormatType { get; }
    }
}

namespace MCM.Abstractions.Attributes
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SettingPropertyGroupAttribute : Attribute
    {
        public SettingPropertyGroupAttribute(string name) { }
        public int GroupOrder { get; set; }
    }
}

namespace MCM.Abstractions.Attributes.v2
{
    public abstract class SettingAttribute : Attribute
    {
        public int Order { get; set; }
        public bool RequireRestart { get; set; }
        public string HintText { get; set; }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SettingPropertyTextAttribute : SettingAttribute
    {
        public SettingPropertyTextAttribute(string name, int order, bool requireRestart, string hintText)
        {
            Order = order;
            RequireRestart = requireRestart;
            HintText = hintText;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SettingPropertyBoolAttribute : SettingAttribute
    {
        public SettingPropertyBoolAttribute(string name) { }
    }
}
