﻿using CsScriptManaged;
using DbgEngManaged;
using System;
using System.Text;

namespace CsScripts
{
    public class Module
    {
        /// <summary>
        /// The module name
        /// </summary>
        private SimpleCache<string> name;

        /// <summary>
        /// The image name
        /// </summary>
        private SimpleCache<string> imageName;

        /// <summary>
        /// The loaded image name
        /// </summary>
        private SimpleCache<string> loadedImageName;

        /// <summary>
        /// The symbol file name
        /// </summary>
        private SimpleCache<string> symbolFileName;

        /// <summary>
        /// The mapped image name
        /// </summary>
        private SimpleCache<string> mappedImageName;

        /// <summary>
        /// Initializes a new instance of the <see cref="Module"/> class.
        /// </summary>
        /// <param name="id">The identifier.</param>
        internal Module(Process process, ulong id)
        {
            Id = id;
            Process = process;
            name = SimpleCache.Create(() => GetName(DebugModname.Module));
            imageName = SimpleCache.Create(() => GetName(DebugModname.Image));
            loadedImageName = SimpleCache.Create(() => GetName(DebugModname.LoadedImage));
            symbolFileName = SimpleCache.Create(() => GetName(DebugModname.SymbolFile));
            mappedImageName = SimpleCache.Create(() => GetName(DebugModname.MappedImage));
            TypesByName = new DictionaryCache<string, CodeType>(GetTypeByName);
            TypesById = new DictionaryCache<uint, CodeType>(GetTypeById);
            GlobalVariables = new DictionaryCache<string, Variable>(GetGlobalVariable);
            UserTypeCastedGlobalVariables = new DictionaryCache<string, Variable>((name) =>
            {
                Variable variable = Process.UserTypeCastedVariables[GlobalVariables[name]];

                if (UserTypeCastedGlobalVariables.Count == 0)
                {
                    GlobalCache.VariablesUserTypeCastedFieldsByName.Add(UserTypeCastedGlobalVariables);
                }

                return variable;
            });
        }

        /// <summary>
        /// Gets the global variable by the name.
        /// </summary>
        /// <param name="name">The name.</param>
        private Variable GetGlobalVariable(string name)
        {
            using (ProcessSwitcher switcher = new ProcessSwitcher(Process))
            {
                uint typeId = Context.SymbolProvider.GetGlobalVariableTypeId(this, name);
                var codeType = TypesById[typeId];
                ulong address = Context.SymbolProvider.GetGlobalVariableAddress(this, name);

                return Variable.CreateNoCast(codeType, address, name);
            }
        }

        /// <summary>
        /// Gets all modules for the current process.
        /// </summary>
        public static Module[] All
        {
            get
            {
                return Process.Current.Modules;
            }
        }

        /// <summary>
        /// Types by the name
        /// </summary>
        internal DictionaryCache<string, CodeType> TypesByName { get; private set; }

        /// <summary>
        /// Types by the identifier
        /// </summary>
        internal DictionaryCache<uint, CodeType> TypesById { get; private set; }

        /// <summary>
        /// Cache of global variables.
        /// </summary>
        internal DictionaryCache<string, Variable> GlobalVariables { get; private set; }

        /// <summary>
        /// Cache of user type casted global variables.
        /// </summary>
        internal DictionaryCache<string, Variable> UserTypeCastedGlobalVariables { get; private set; }

        /// <summary>
        /// Gets the identifier.
        /// </summary>
        public ulong Id { get; private set; }

        /// <summary>
        /// Gets the owning process.
        /// </summary>
        public Process Process { get; private set; }

        /// <summary>
        /// Gets the offset (address location of module base).
        /// </summary>
        public ulong Offset
        {
            get
            {
                return Id;
            }
        }

        /// <summary>
        /// Gets the module name. This is usually just the file name without the extension. In a few cases,
        /// the module name differs significantly from the file name.
        /// </summary>
        public string Name
        {
            get
            {
                return name.Value;
            }

            internal set
            {
                name.Value = value;
            }
        }

        /// <summary>
        /// Gets the name of the image. This is the name of the executable file, including the extension.
        /// Typically, the full path is included in user mode but not in kernel mode.
        /// </summary>
        public string ImageName
        {
            get
            {
                return imageName.Value;
            }
        }

        /// <summary>
        /// Gets the name of the loaded image. Unless Microsoft CodeView symbols are present, this is the same as the image name.
        /// </summary>
        public string LoadedImageName
        {
            get
            {
                return loadedImageName.Value;
            }
        }

        /// <summary>
        /// Gets the name of the symbol file. The path and name of the symbol file. If no symbols have been loaded,
        /// this is the name of the executable file instead.
        /// </summary>
        public string SymbolFileName
        {
            get
            {
                return symbolFileName.Value;
            }
        }

        /// <summary>
        /// Gets the name of the mapped image. In most cases, this is NULL. If the debugger is mapping an image file
        /// (for example, during minidump debugging), this is the name of the mapped image.
        /// </summary>
        public string MappedImageName
        {
            get
            {
                return mappedImageName.Value;
            }
        }

        /// <summary>
        /// Gets the variable.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns>Variable if found</returns>
        /// <exception cref="System.ArgumentException">Variable name contains wrong module name. Don't add it manually, it will be added automatically.</exception>
        public Variable GetVariable(string name)
        {
            using (ProcessSwitcher switcher = new ProcessSwitcher(Process))
            {
                int moduleIndex = name.IndexOf('!');

                if (moduleIndex > 0)
                {
                    if (string.Compare(name, 0, Name, 0, Math.Max(Name.Length, moduleIndex), true) != 0)
                    {
                        throw new ArgumentException("Variable name contains wrong module name. Don't add it manually, it will be added automatically.");
                    }

                    name = name.Substring(moduleIndex + 1);
                }

                return UserTypeCastedGlobalVariables[name];
            }
        }

        #region Cache filling functions
        /// <summary>
        /// Gets the name of the module.
        /// </summary>
        /// <param name="modname">The type of module name.</param>
        /// <returns>Read name</returns>
        private string GetName(DebugModname modname)
        {
            uint nameSize;
            StringBuilder sb = new StringBuilder(Constants.MaxFileName);

            Context.Symbols.GetModuleNameStringWide((uint)modname, 0xffffffff, Id, sb, (uint)sb.Capacity, out nameSize);

            string name = sb.ToString();

            Process.Current.UpdateModuleByNameCache(this, name);
            return name;
        }

        /// <summary>
        /// Gets the type with the specified name.
        /// </summary>
        /// <param name="name">The name.</param>
        private CodeType GetTypeByName(string name)
        {
            using (ProcessSwitcher switcher = new ProcessSwitcher(Process))
            {
                int moduleIndex = name.IndexOf('!');

                if (moduleIndex > 0)
                {
                    if (string.Compare(name.Substring(0, moduleIndex), Name, true) != 0)
                    {
                        throw new ArgumentException("Type name contains wrong module name. Don't add it manually, it will be added automatically.");
                    }

                    name = name.Substring(moduleIndex + 1);
                }

                uint typeId = Context.SymbolProvider.GetTypeId(this, name);

                return TypesById[typeId];
            }
        }

        /// <summary>
        /// Gets the type with the specified identifier.
        /// </summary>
        /// <param name="typeId">The type identifier.</param>
        private CodeType GetTypeById(uint typeId)
        {
            return new CodeType(this, typeId, Context.SymbolProvider.GetTypeTag(this, typeId));
        }
        #endregion
    }
}
