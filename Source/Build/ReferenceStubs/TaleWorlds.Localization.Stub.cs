using System.Collections.Generic;
using System.Reflection;

[assembly: AssemblyVersion("1.0.0.0")]

namespace TaleWorlds.Localization
{
    public class TextObject
    {
        public TextObject(string value = "", Dictionary<string, object> attributes = null) { }
        public override string ToString() { return string.Empty; }
    }
}
