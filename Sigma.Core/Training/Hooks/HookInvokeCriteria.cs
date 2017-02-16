﻿/* 
MIT License

Copyright (c) 2016-2017 Florian Cäsar, Michael Plainer

For full license see LICENSE in the root directory of this project. 
*/

using Sigma.Core.Utils;
using System;
using System.ComponentModel;

namespace Sigma.Core.Training.Hooks
{
	/// <summary>
	/// A base hook invoke criteria for additionally limiting hook invocations to certain criteria.
	/// </summary>
	public abstract class HookInvokeCriteria
	{
		/// <summary>
		/// The registry entries required from the base hook to check this criteria.
		/// </summary>
		internal string[] RequiredRegistryEntries { get; }

		/// <summary>
		/// An array of flags indicating if the corresponding registry entry is simple and direct (i.e. not nested, no dot notation).
		/// </summary>
		internal bool[] SimpleDirectEntries { get; }

		/// <summary>
		/// The internal parameter registry (used for criteria comparison and external access).
		/// Note: Please us this registry instead of private members. 
		/// </summary>
		internal IRegistry ParameterRegistry { get; }

		/// <summary>
		/// Create a hook criteria that optionally requires registry entries from the base hook.
		/// </summary>
		/// <param name="requiredRegistryEntries"></param>
		protected HookInvokeCriteria(params string[] requiredRegistryEntries)
		{
			if (requiredRegistryEntries == null) throw new ArgumentNullException(nameof(requiredRegistryEntries));

			SimpleDirectEntries = new bool[requiredRegistryEntries.Length];
			for (var i = 0; i < requiredRegistryEntries.Length; i++)
			{
				SimpleDirectEntries[i] = !requiredRegistryEntries[i].Contains(".");
			}

			RequiredRegistryEntries = requiredRegistryEntries;
			ParameterRegistry = new Registry();
			ParameterRegistry["required_registry_entries"] = requiredRegistryEntries;
		}

		/// <summary>
		/// Create a criteria that fires if this criteria fires a certain amount of <see cref="times"/>.
		/// </summary>
		/// <param name="times">The times (they are a changing).</param>
		/// <returns>A repeat criteria as specified.</returns>
		public RepeatCriteria Repeated(int times)
		{
			return new RepeatCriteria(this, times);
		}

		/// <summary>
		/// Check if the criteria is satisfied using a certain parameter registry and helper resolver from the base hook.
		/// </summary>
		/// <param name="registry">The registry.</param>
		/// <param name="resolver">The helper resolver.</param>
		/// <returns>A boolean indicating if this criteria is satisfied.</returns>
		public abstract bool CheckCriteria(IRegistry registry, IRegistryResolver resolver);

		/// <summary>
		/// Check if this criteria functionally equals another criteria.
		/// Note: The base implementation checks for same type and parameter registry.
		/// </summary>
		/// <param name="other">The other criteria.</param>
		/// <returns>A boolean indicating if this criteria functionally equals another criteria.</returns>
		internal virtual bool FunctionallyEquals(HookInvokeCriteria other)
		{
			return GetType() == other.GetType() && ParameterRegistry.RegistryContentEquals(other.ParameterRegistry);
		}
	}

	/// <summary>
	/// A repeat criteria that fires if the underlying criteria fires a certain amount of times.
	/// </summary>
	public class RepeatCriteria : HookInvokeCriteria
	{
		/// <summary>
		/// Create a repeat criteria for a certain amount of repeated fires of a base criteria.
		/// </summary>
		/// <param name="repetitions"></param>
		/// <param name="criteria"></param>
		public RepeatCriteria(HookInvokeCriteria criteria, int repetitions)
		{
			if (criteria == null) throw new ArgumentNullException(nameof(criteria));
			if (repetitions <= 0) throw new ArgumentOutOfRangeException($"{nameof(repetitions)} must be > 0.");

			ParameterRegistry["base_criteria"] = criteria;
			ParameterRegistry["target_repetitions"] = repetitions;
			ParameterRegistry["current_repetitions"] = 0;
		}

		public override bool CheckCriteria(IRegistry registry, IRegistryResolver resolver)
		{
			HookInvokeCriteria baseCriteria = ParameterRegistry.Get<HookInvokeCriteria>("base_criteria");
			int targetRepetitions = ParameterRegistry.Get<int>("target_repetitions");
			int currentRepititions = ParameterRegistry.Get<int>("current_repetitions");

			if (baseCriteria.CheckCriteria(registry, resolver))
			{
				currentRepititions++;
			}

			bool fire = currentRepititions >= targetRepetitions;

			if (fire)
			{
				currentRepititions = 0;
			}

			ParameterRegistry["current_repetitions"] = currentRepititions;

			return fire;
		}

		public override string ToString()
		{
			return $"repeat criteria {ParameterRegistry.Get<HookInvokeCriteria>("base_criteria")} {ParameterRegistry.Get<int>("target_repetitions")} times";
		}
	}

	/// <summary>
	/// A threshold criteria that fires when a certain threshold is reached (once or continuously as specified). 
	/// </summary>
	public class ThresholdCriteria : HookInvokeCriteria
	{
		/// <summary>
		/// Create a threshold criteria that fires when a certain parameter reaches a certain threshold (once or continuously).
		/// </summary>
		/// <param name="parameter">The parameter (identifier) to check.</param>
		/// <param name="target">The target threshold comparison (smaller / greater / equals).</param>
		/// <param name="thresholdValue">The threshold value to compare against.</param>
		/// <param name="fireContinously">Optionally indicate if this criteria should fire once or continuously when the threshold is reached.</param>
		public ThresholdCriteria(string parameter, ComparisonTarget target, double thresholdValue, bool fireContinously = true) : base(parameter)
		{
			if (parameter == null) throw new ArgumentNullException(nameof(parameter));
			if (!Enum.IsDefined(typeof(ComparisonTarget), target)) throw new InvalidEnumArgumentException(nameof(target), (int) target, typeof(ComparisonTarget));

			ParameterRegistry["parameter_identifier"] = parameter;
			ParameterRegistry["target"] = target;
			ParameterRegistry["threshold_value"] = thresholdValue;
			ParameterRegistry["fire_continously"] = fireContinously;
			ParameterRegistry["last_check_met"] = false;
			ParameterRegistry["threshold_reached"] = false;
		}

		public override bool CheckCriteria(IRegistry registry, IRegistryResolver resolver)
		{
			string parameter = ParameterRegistry.Get<string>("parameter_identifier");
			object rawValue = SimpleDirectEntries[0] ? registry.Get(parameter) : resolver.ResolveGetSingle<object>(parameter);
			double value = (double) Convert.ChangeType(rawValue, typeof(double));
			bool thresholdReached = _InternalThresholdReached(value, ParameterRegistry.Get<double>("threshold_value"), ParameterRegistry.Get<ComparisonTarget>("target"));
			bool fire = thresholdReached && (!ParameterRegistry.Get<bool>("last_check_met") || ParameterRegistry.Get<bool>("fire_continously"));

			ParameterRegistry["last_check_met"] = thresholdReached;

			return fire;
		}

		private bool _InternalThresholdReached(double value, double threshold, ComparisonTarget target)
		{
			switch (target)
			{
				case ComparisonTarget.Equals:
					return value == threshold;
				case ComparisonTarget.GreaterThanEquals:
					return value >= threshold;
				case ComparisonTarget.GreaterThan:
					return value > threshold;
				case ComparisonTarget.SmallerThanEquals:
					return value <= threshold;
				case ComparisonTarget.SmallerThan:
					return value < threshold;
				default:
					throw new ArgumentOutOfRangeException($"Comparison target is out of range ({target}), use the provided {nameof(ComparisonTarget)} enum.");
			}
		}

		public override string ToString()
		{
			return $"threshold criteria for \"{ParameterRegistry.Get<string>("parameter_identifier")}\" when {ParameterRegistry.Get<ComparisonTarget>("target")} {ParameterRegistry.Get<double>("threshold_value")}";
		}
	}

	/// <summary>
	/// An extrema criteria that fires when a value has reached a new extrema (min / max).
	/// </summary>
	public class ExtremaCriteria : HookInvokeCriteria
	{
		/// <summary>
		/// Create an extrema criteria that fires when a certain parameter is at a certain extrema (min / max).
		/// </summary>
		/// <param name="parameter">The parameter (identifier) to check.</param>
		/// <param name="target">The target extrema (min / max).</param>
		public ExtremaCriteria(string parameter, ExtremaTarget target) : base(parameter)
		{
			if (parameter == null) throw new ArgumentNullException(nameof(parameter));
			if (!Enum.IsDefined(typeof(ExtremaTarget), target)) throw new InvalidEnumArgumentException(nameof(target), (int) target, typeof(ExtremaTarget));

			ParameterRegistry["parameter_identifier"] = parameter;
			ParameterRegistry["target"] = target;
			ParameterRegistry["current_extremum"] = double.NaN;
		}

		public override bool CheckCriteria(IRegistry registry, IRegistryResolver resolver)
		{
			ExtremaTarget target = ParameterRegistry.Get<ExtremaTarget>("target");
			string parameter = ParameterRegistry.Get<string>("parameter_identifier");
			double value = SimpleDirectEntries[0] ? registry.Get<double>(parameter) : resolver.ResolveGetSingle<double>(parameter);
			double currentExtremum = ParameterRegistry.Get<double>("current_extremum");
			bool reachedExtremum = target == ExtremaTarget.Min && value < currentExtremum || target == ExtremaTarget.Max && value > currentExtremum;

			if (double.IsNaN(currentExtremum) || reachedExtremum)
			{
				ParameterRegistry["current_extremum"] = value;

				return true;
			}

			return false;
		}

		public override string ToString()
		{
			return $"extrema criteria for \"{ParameterRegistry.Get<string>("parameter_identifier")}\" when at \"{ParameterRegistry.Get<ExtremaTarget>("target")}";
		}
	}

	/// <summary>
	/// A comparison target for conditional invokes. 
	/// </summary>
	public enum ComparisonTarget { GreaterThan, GreaterThanEquals, Equals, SmallerThanEquals, SmallerThan }

	/// <summary>
	/// An extrema target for conditional invokes.
	/// </summary>
	public enum ExtremaTarget { Max, Min }
}