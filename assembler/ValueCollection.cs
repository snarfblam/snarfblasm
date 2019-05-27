using System;
using System.Collections.Generic;
using System.Text;

namespace snarfblasm.assembler
{
    class PassValuesCollection
    {
        Dictionary<NamespacedLabelName, PassValue> Values = new Dictionary<NamespacedLabelName, PassValue>(new LabelNameComparer());

        /// <summary>Provides a case-insensitive comparer to look up values by name</summary>
        private class LabelNameComparer : IEqualityComparer<NamespacedLabelName>
        {
            static StringComparer comparer = StringComparer.OrdinalIgnoreCase;

            public bool Equals(NamespacedLabelName x, NamespacedLabelName y) {
                if (!comparer.Equals(x.nspace ?? string.Empty, y.nspace ?? string.Empty)) return false;
                return comparer.Equals(x.name ?? string.Empty, y.name ?? string.Empty);
            }

            public int GetHashCode(NamespacedLabelName obj) {
                return comparer.GetHashCode(obj.nspace ?? string.Empty) ^ comparer.GetHashCode(obj.name ?? string.Empty);
            }
        }

        /// <summary>A non-empty string indicating the current namespace, or null to indicate the current namespace is the default namespace</summary>
        public string CurrentNamespace { get; set; }
        public List<string> _NamespaceImports = new List<string>();
        public IList<string> NamespaceImports { get { return _NamespaceImports; } }

        public PassValuesCollection() {
        }

        /// <summary>Returns the value that exactly matches the specified name</summary>
        public LiteralValue this[NamespacedLabelName name] {
            get {
                return Values[name].value;
            }
        }

        public LiteralValue? TryGetValue(NamespacedLabelName name) {
            PassValue result = default(PassValue);
            bool found = false;

            if (name.IsSimple) {
                var fullName = new NamespacedLabelName(name.name, CurrentNamespace);
                // Current namespace is highest precedence
                if (!string.IsNullOrEmpty(CurrentNamespace)) {
                    found = Values.TryGetValue(name, out result);
                }
                // Default namespace is next precedence
                if (!found) {
                    fullName = new NamespacedLabelName(name.name, null);
                    found = Values.TryGetValue(name, out result);

                    // Imported namespace is lowest precedence
                    if (!found) {
                        // Todo: namespace imports
                    }
                }
            } else {
                found = Values.TryGetValue(name, out result);
            }

            return found ? (LiteralValue?)result.value : null;
        }

        public void SetValue(NamespacedLabelName name, LiteralValue value, bool isFixed, out bool valueIsFixedError) {
            valueIsFixedError = false;

            if (name.IsSimple) {
                // Current namespace
                if (!string.IsNullOrEmpty(CurrentNamespace)) {
                    name = new NamespacedLabelName(name.name, CurrentNamespace);
                    if (SetValue_internal(name, value, isFixed, out valueIsFixedError, true)) return;
                }
                
                // Default namespace
                name = new NamespacedLabelName(name.name, null);
                if(SetValue_internal(name, value, isFixed, out valueIsFixedError, true)) return;

                // Todo: imported namespaces

                // If neither is found, the new variable should go into the current namespace
                string currentNs = null; // deafult to, well, the default namespace
                if(!string.IsNullOrEmpty(CurrentNamespace)) currentNs = CurrentNamespace;
                name = new NamespacedLabelName(name.name,currentNs);
                SetValue_internal(name, value, isFixed, out valueIsFixedError, false);
            } else {
                SetValue_internal(name, value, isFixed, out valueIsFixedError, false);
            }
        }

        /// <summary></summary>
        /// <param name="name">A FULLY QUALIFIED variable name</param>
        /// <param name="value">Value to assign</param>
        /// <param name="isFixed">Whether the value will be fixed (immutable)</param>
        /// <param name="valueIsFixedError">Returns true if an attempt was made to re-assign a fixed value</param>
        /// <param name="mustExist">If true, the value will only be set if it already exists.</param>
        /// <returns>Whether or not an attempt was made to set the value. If an attempt was made, but there was an error, valueIsFixedError will be set to true and the return value will be true.</returns>
        private bool SetValue_internal(NamespacedLabelName name, LiteralValue value, bool isFixed, out bool valueIsFixedError, bool mustExist) {
            valueIsFixedError = false;

            PassValue existingValue;
            bool exists = Values.TryGetValue(name, out existingValue);

            if (exists){
                if (existingValue.isFixed) {
                    valueIsFixedError = true;
                    return false;
                }
            } else {
                if (mustExist) return false;
            }

            Values[name] = new PassValue(value, isFixed);
            return true;
        }


        // Todo: RemoveValue is unused. Update to deal with namespaces or remove.
        //public bool RemoveValue(NamespacedLabelName name, out bool valueIsFixedError) {
        //    PassValue existingValue;
        //    bool exists = Values.TryGetValue(name, out existingValue);

        //    if (exists) {
        //        if (existingValue.isFixed) {
        //            valueIsFixedError = true;
        //            return false;
        //        } else {
        //            Values.Remove(name);
        //            valueIsFixedError = false;
        //            return true;
        //        }
        //    }

        //    valueIsFixedError = false;
        //    return false;
        //}
        private struct PassValue
        {
            public PassValue(LiteralValue value, bool isFixed) {
                this.value = value;
                this.isFixed = isFixed;
            }
            public LiteralValue value;
            public bool isFixed;
        }

        internal bool NameExists(NamespacedLabelName name) {
            return Values.ContainsKey(name);
        }
    }

}
