using System;
using System.Collections.Generic;
using System.Text;

/*
 * Some notes about these classes:
 * 
 * Values are stored as a linked list of ValueLink objects. These are exposed through the PassValue struct,
 * which allows us to present instance methods that can operate on null values and handle scenarios where
 * the underlying linked must be created.
 * 
 * However when the linked list is created, the dictionary must still be updated to reflect this. (From then
 * on, changes made to that PassValue are done by mutating the underlying linked list, so no further modifications
 * to the dictionary are necessary). I.e.
 * 
 *   var value = Find(myVariableName);   // Find may return a null reference
 *   var newValue = value.SetValue(...); // SetValue may return a new object
 *   if (value != newValue)              // Avoid the cost of a lookup, if possible
 *       dictionary[myVariableName] = newValue
 *       
 * I know this design is bad and I should feel bad. It's the best of multiple solutions I've attempted,
 * I just can't spend forever on this one part.
 */


namespace snarfblasm.assembler
{
    class PassValuesCollection
    {
        Dictionary<string, PassValue> Values = new Dictionary<string, PassValue>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A non-empty string indicating the current namespace, or null to indicate the current namespace is the default namespace</summary>
        public string CurrentNamespace { get; set; }
        public List<string> _NamespaceImports = new List<string>();
        public IList<string> NamespaceImports { get { return _NamespaceImports; } }

        public PassValuesCollection() {
        }

        private PassValue Find(string name) {
            PassValue result;
            return (Values.TryGetValue(name, out result)) ? result : default(PassValue);

        }

        /// <summary>Returns the value that exactly matches the specified name</summary>
        public LiteralValue this[NamespacedLabelName name] {
            get {
                return Find(name.name).GetValue(name.nspace).Value; // Throws error if value does not exist
            }
        }

        public LiteralValue? TryGetValue(NamespacedLabelName name) {
            return Find(name.name).GetValue(name.nspace);
        }

        public void SetValue(NamespacedLabelName name, LiteralValue value, bool isFixed, out bool valueIsFixedError) {
            valueIsFixedError = false;

            PassValue oldValue = Find(name.name);
            PassValue newValue;

            if (name.IsSimple) {
                newValue = oldValue.SetValueWithFallback(CurrentNamespace, value, isFixed, out valueIsFixedError);
            } else {
                newValue = oldValue.SetValue(name.nspace, value, isFixed, out valueIsFixedError);
            }

            if (oldValue != newValue) {
                Values[name.name] = newValue;
            }
        }



        internal bool NameExists(NamespacedLabelName name) {
            return null != TryGetValue(name);
        }
    }

    struct PassValue
    {
        ValueLink value;

        public LiteralValue? GetValue(string nspace) {
            return ValueLink.GetValue(value, nspace);
        }

        public LiteralValue? GetValue(string nspace, bool fallbackToDefaultNamespace) {
            return ValueLink.GetValue(value, nspace, fallbackToDefaultNamespace);
        }

        public PassValue SetValue(string nspace, LiteralValue value, bool isFixed, out bool writeToFixedError) {
            PassValue result;
            result.value = ValueLink.SetValue(this.value, nspace, value, isFixed, out writeToFixedError);
            return result;
        }
        /// <summary>Attempts to overwrite an existing value in either the specified namespace or the default namespace,
        /// or creates a new value in the specified namespace if an existing value can not be found.</summary>
        public PassValue SetValueWithFallback(string nspace, LiteralValue value, bool? isFixed, out bool writeToFixedError) {
            PassValue result;
            result.value = ValueLink.SetValueWithFallback(this.value, nspace, value, isFixed, out writeToFixedError);
            return result;

        }

        public bool Equals(PassValue v) {
            return this.value == v.value;
        }

        // We aren't overriding object.Equals because our implemenation would be exactly the same as the inherited

        public static bool operator ==(PassValue a, PassValue b) {
            return a.value == b.value;
        }
        public static bool operator !=(PassValue a, PassValue b) {
            return a.value != b.value;
        }

        public override int GetHashCode() {
            return value.GetHashCode();
        }

        /// <summary>
        /// Linked list which contains all values for a given name in all namespaces
        /// </summary>
        private class ValueLink
        {
            private ValueLink(string nspace, LiteralValue value, bool isFixed) {
            }

            public static ValueLink Append(ValueLink current, string nspace, LiteralValue value, bool isFixed) {
                var newLink = new ValueLink(nspace, value, isFixed);
                if (current == null) return newLink;

                // Add new link to end of chain
                var link = current;
                while (link.next != null) {
                    link = link.next;
                }
                link.next = newLink;

                return current;
            }
            public static LiteralValue? GetValue(ValueLink link, string nspace) {
                while (link != null) {
                    if (link.Nspace == nspace) {
                        return link.Value;
                    }
                    link = link.next;
                }

                return null;
            }
            public static LiteralValue? GetValue(ValueLink link, string nspace, bool fallBackToDefaultNamespace) {
                LiteralValue? fallback = null;

                while (link != null) {
                    if (link.Nspace == nspace) {
                        return link.Value;
                    } else if (link.Nspace == null) {
                        fallback = link.Value;
                    }
                    link = link.next;
                }

                return fallBackToDefaultNamespace ? fallback : null;
            }

            /// <summary>Writes the value. Returns whether the value was successfully written. A new ValueLink
            /// is created if the passed in value was null.</summary>
            public static ValueLink SetValue(ValueLink linke, string nspace, LiteralValue value, bool isFixed, out bool writeToFixedError) {
                writeToFixedError = false;

                if (linke == null) {
                    return new ValueLink(nspace, value, isFixed);
                }

                var thisLink = linke;

                while (true) { // yup
                    if (thisLink.Nspace == nspace) {
                        if (thisLink.IsFixed) {
                            writeToFixedError = true;
                            return linke;
                        } else {
                            thisLink.Value = value;
                            thisLink.IsFixed = isFixed;
                        }
                    }

                    var next = thisLink.next;
                    if (next == null) {
                        Append(thisLink, nspace, value, isFixed);
                        return linke;
                    }

                    thisLink = next;
                }
            }

            /// <summary>Writes the value. This method will prefer updating a value in the default namespace over
            /// creating a new value in the specified namespace. A new ValueLink is created if the passed in value
            /// was null.</summary>
            public static ValueLink SetValueWithFallback(ValueLink link, string nspace, LiteralValue value, bool? isFixed, out bool writeToFixedError) {
                writeToFixedError = false;

                // Precendence is as follows:
                // 1. Overwrite existing value in specified namespace
                // 2. Overwrite existing value in default namespace
                // 3. Create new value in specified namespace

                if (link == null) {
                    return new ValueLink(nspace, value, isFixed ?? false);
                }

                ValueLink defaultLink = null;

                var thisLink = link;
                while (true) { // yup
                    if (thisLink.Nspace == nspace) {
                        // Case 1
                        if (thisLink.IsFixed) {
                            writeToFixedError = true;
                            return link;
                        } else {
                            thisLink.Value = value;
                            if (isFixed != null) thisLink.IsFixed = isFixed.Value;
                        }
                    } else if (thisLink.Nspace == null) {
                        defaultLink = thisLink;
                    }

                    var next = thisLink.next;
                    if (next == null) {
                        if (defaultLink == null) {
                            // Case 3
                            Append(thisLink, nspace, value, isFixed ?? false); // I guess if 'isFixed' is not specified, we default to false
                            return link;
                        } else {
                            // Case 2
                            if (defaultLink.IsFixed) {
                                writeToFixedError = true;
                                return link;
                            } else {
                                defaultLink.Value = value;
                                if (isFixed != null) defaultLink.IsFixed = isFixed.Value;
                            }
                        }
                    }

                    link = next;
                }
            }


            public string Nspace { get; private set; }
            public LiteralValue Value { get; private set; }
            public bool IsFixed { get; private set; }

            ValueLink next;
        }

    }



    //class PassValuesCollection
    //{
    //    Dictionary<NamespacedLabelName, PassValue> Values = new Dictionary<NamespacedLabelName, PassValue>(new LabelNameComparer());

    //    /// <summary>Provides a case-insensitive comparer to look up values by name</summary>
    //    private class LabelNameComparer : IEqualityComparer<NamespacedLabelName>
    //    {
    //        static StringComparer comparer = StringComparer.OrdinalIgnoreCase;

    //        public bool Equals(NamespacedLabelName x, NamespacedLabelName y) {
    //            if (!comparer.Equals(x.nspace ?? string.Empty, y.nspace ?? string.Empty)) return false;
    //            return comparer.Equals(x.name ?? string.Empty, y.name ?? string.Empty);
    //        }

    //        public int GetHashCode(NamespacedLabelName obj) {
    //            return comparer.GetHashCode(obj.nspace ?? string.Empty) ^ comparer.GetHashCode(obj.name ?? string.Empty);
    //        }
    //    }

    //    /// <summary>A non-empty string indicating the current namespace, or null to indicate the current namespace is the default namespace</summary>
    //    public string CurrentNamespace { get; set; }
    //    public List<string> _NamespaceImports = new List<string>();
    //    public IList<string> NamespaceImports { get { return _NamespaceImports; } }

    //    public PassValuesCollection() {
    //    }

    //    /// <summary>Returns the value that exactly matches the specified name</summary>
    //    public LiteralValue this[NamespacedLabelName name] {
    //        get {
    //            return Values[name].value;
    //        }
    //    }

    //    public LiteralValue? TryGetValue(NamespacedLabelName name) {
    //        PassValue result = default(PassValue);
    //        bool found = false;

    //        if (name.IsSimple) {
    //            var fullName = new NamespacedLabelName(name.name, CurrentNamespace);
    //            // Current namespace is highest precedence
    //            if (!string.IsNullOrEmpty(CurrentNamespace)) {
    //                found = Values.TryGetValue(name, out result);
    //            }
    //            // Default namespace is next precedence
    //            if (!found) {
    //                fullName = new NamespacedLabelName(name.name, null);
    //                found = Values.TryGetValue(name, out result);

    //                // Imported namespace is lowest precedence
    //                if (!found) {
    //                    // Todo: namespace imports
    //                }
    //            }
    //        } else {
    //            found = Values.TryGetValue(name, out result);
    //        }

    //        return found ? (LiteralValue?)result.value : null;
    //    }

    //    public void SetValue(NamespacedLabelName name, LiteralValue value, bool isFixed, out bool valueIsFixedError) {
    //        valueIsFixedError = false;

    //        if (name.IsSimple) {
    //            // Current namespace
    //            if (!string.IsNullOrEmpty(CurrentNamespace)) {
    //                name = new NamespacedLabelName(name.name, CurrentNamespace);
    //                if (SetValue_internal(name, value, isFixed, out valueIsFixedError, true)) return;
    //            }
                
    //            // Default namespace
    //            name = new NamespacedLabelName(name.name, null);
    //            if(SetValue_internal(name, value, isFixed, out valueIsFixedError, true)) return;

    //            // Todo: imported namespaces

    //            // If neither is found, the new variable should go into the current namespace
    //            string currentNs = null; // deafult to, well, the default namespace
    //            if(!string.IsNullOrEmpty(CurrentNamespace)) currentNs = CurrentNamespace;
    //            name = new NamespacedLabelName(name.name,currentNs);
    //            SetValue_internal(name, value, isFixed, out valueIsFixedError, false);
    //        } else {
    //            SetValue_internal(name, value, isFixed, out valueIsFixedError, false);
    //        }
    //    }

    //    /// <summary></summary>
    //    /// <param name="name">A FULLY QUALIFIED variable name</param>
    //    /// <param name="value">Value to assign</param>
    //    /// <param name="isFixed">Whether the value will be fixed (immutable)</param>
    //    /// <param name="valueIsFixedError">Returns true if an attempt was made to re-assign a fixed value</param>
    //    /// <param name="mustExist">If true, the value will only be set if it already exists.</param>
    //    /// <returns>Whether or not an attempt was made to set the value. If an attempt was made, but there was an error, valueIsFixedError will be set to true and the return value will be true.</returns>
    //    private bool SetValue_internal(NamespacedLabelName name, LiteralValue value, bool isFixed, out bool valueIsFixedError, bool mustExist) {
    //        valueIsFixedError = false;

    //        PassValue existingValue;
    //        bool exists = Values.TryGetValue(name, out existingValue);

    //        if (exists){
    //            if (existingValue.isFixed) {
    //                valueIsFixedError = true;
    //                return false;
    //            }
    //        } else {
    //            if (mustExist) return false;
    //        }

    //        Values[name] = new PassValue(value, isFixed);
    //        return true;
    //    }


    //    // Todo: RemoveValue is unused. Update to deal with namespaces or remove.
    //    //public bool RemoveValue(NamespacedLabelName name, out bool valueIsFixedError) {
    //    //    PassValue existingValue;
    //    //    bool exists = Values.TryGetValue(name, out existingValue);

    //    //    if (exists) {
    //    //        if (existingValue.isFixed) {
    //    //            valueIsFixedError = true;
    //    //            return false;
    //    //        } else {
    //    //            Values.Remove(name);
    //    //            valueIsFixedError = false;
    //    //            return true;
    //    //        }
    //    //    }

    //    //    valueIsFixedError = false;
    //    //    return false;
    //    //}
    //    private struct PassValue
    //    {
    //        public PassValue(LiteralValue value, bool isFixed) {
    //            this.value = value;
    //            this.isFixed = isFixed;
    //        }
    //        public LiteralValue value;
    //        public bool isFixed;
    //    }

    //    internal bool NameExists(NamespacedLabelName name) {
    //        return Values.ContainsKey(name);
    //    }
    //}

}
