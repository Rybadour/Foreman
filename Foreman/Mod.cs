using System;
using System.Collections.Generic;
using System.Linq;

namespace Foreman
{
	public class Mod
	{
		public String Id = "";
		public String Name = "";
		public String title = "";
		public String version = "";
		public Version parsedVersion;
		public String dir = "";
		public String description = "";
		public String author = "";
		public List<String> dependencies = new List<String>();
		public List<ModDependency> parsedDependencies = new List<ModDependency>();
		public bool Enabled = true;

		public bool SatisfiesDependency(ModDependency dep)
		{
			if (Name != dep.ModName)
			{
				return false;
			}
			if (dep.Version != null)
			{
				bool versionMatch = true;
				switch (dep.VersionOperator)
				{
					case VersionOperator.EqualTo:
						versionMatch = parsedVersion == dep.Version;
						break;
					case VersionOperator.GreaterThan:
						versionMatch = parsedVersion > dep.Version;
						break;
					case VersionOperator.GreaterThanOrEqual:
						versionMatch = parsedVersion >= dep.Version;
						break;
					case VersionOperator.LessThan:
						versionMatch = parsedVersion < dep.Version;
						break;
					case VersionOperator.LessThanOrEqual:
						versionMatch = parsedVersion <= dep.Version;
						break;
				}

				if (!versionMatch)
					return false;
				
			}
			return true;
		}

		public bool DependsOn(Mod mod, bool ignoreOptional)
		{
			IEnumerable<ModDependency> depList;
			if (ignoreOptional)
			{
				depList = parsedDependencies.Where(d => !d.Optional);
			} else {
				depList = parsedDependencies;
			}
			foreach (ModDependency dep in depList)
			{
				if (mod.SatisfiesDependency(dep))
				{
					return true;
				}
			}
			return false;
		}

		public override String ToString()
		{
			return Name;
		}
	}

	public class ModDependency
	{
		public DependencyType Type = DependencyType.Required;
		public String ModName = "";
		public Version Version;
		public VersionOperator VersionOperator = VersionOperator.EqualTo;

		public bool Optional =>
			Type == DependencyType.Optional ||
			Type == DependencyType.OptionalHidden ||
			Type == DependencyType.Incompatible;

		public override String ToString()
		{
			return ModName + " " + VersionOperator.Token() + " " + Version;
		}
	}

	public enum VersionOperator
	{
		EqualTo,
		GreaterThan,
		GreaterThanOrEqual,
		LessThan,
		LessThanOrEqual
	}

	public enum DependencyType
	{
		Required,
		Optional,
		OptionalHidden,
		Incompatible
	}
}
