using System;
using System.Collections.Generic;
using UnityEngine;

namespace cherrydev
{
    public class DialogExternalFunctionsHandler
    {
        public delegate object ExternalFunction();

        private readonly Dictionary<string, ExternalFunction> _externals = new();
        private readonly Dictionary<string, Action<string>> _prefixExternals = new();

        public ExternalFunction CallExternalFunction(string funcName)
        {
            if (_externals.ContainsKey(funcName))
            {
                ExternalFunction external = _externals[funcName];
                external?.Invoke();

                return _externals[funcName];
            }
            foreach (KeyValuePair<string, Action<string>> prefixExternal in _prefixExternals)
            {
                if (funcName.StartsWith(prefixExternal.Key, StringComparison.Ordinal))
                {
                    prefixExternal.Value?.Invoke(funcName);
                    return null;
                }
            }

            Debug.LogWarning($"There is no function with name '{funcName}'");
            return null;
        }

        public void BindExternalFunction(string funcName, Action function)
        {
            BindExternalFunctionBase(funcName, () =>
            {
                function();
                return null;
            });
        }

        public void UnbindExternalFunction(string funcName)
        {
            if (_externals.ContainsKey(funcName))
                _externals.Remove(funcName);
        }

        public void BindExternalFunctionPrefix(string prefix, Action<string> function)
        {
            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentException("External function prefix cannot be null or empty.", nameof(prefix));

            if (_prefixExternals.ContainsKey(prefix))
                _prefixExternals.Remove(prefix);

            _prefixExternals[prefix] = function;
        }

        public void UnbindExternalFunctionPrefix(string prefix)
        {
            if (_prefixExternals.ContainsKey(prefix))
                _prefixExternals.Remove(prefix);
        }

        private void BindExternalFunctionBase(string funcName, ExternalFunction externalFunction)
        {
            if (_externals.ContainsKey(funcName))
                _externals.Remove(funcName);

            _externals[funcName] = externalFunction;
        }
    }
}
