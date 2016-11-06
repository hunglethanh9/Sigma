﻿/* 
MIT License

Copyright (c) 2016 Florian Cäsar, Michael Plainer

For full license see LICENSE in the root directory of this project. 
*/

using log4net;
using Sigma.Core.Data.Preprocessors;
using Sigma.Core.Data.Readers;
using Sigma.Core.Handlers;
using Sigma.Core.Math;
using Sigma.Core.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Sigma.Core.Data.Extractors
{
	public class CSVRecordExtractor : IRecordExtractor
	{
		private ILog logger = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

		private Dictionary<string, IList<int>> namedColumnIndexMappings;
		private Dictionary<int, Dictionary<object, object>> columnValueMappings;

		public IRecordReader Reader
		{
			get; set;
		}

		public CSVRecordExtractor(Dictionary<string, int[][]> namedColumnIndexMappings, Dictionary<int, Dictionary<object, object>> columnValueMappings = null) : this(ArrayUtils.GetFlatColumnMappings(namedColumnIndexMappings))
		{
		}

		public CSVRecordExtractor(Dictionary<string, IList<int>> namedColumnIndexMappings, Dictionary<int, Dictionary<object, object>> columnValueMappings = null)
		{
			this.namedColumnIndexMappings = namedColumnIndexMappings;

			if (columnValueMappings == null)
			{
				columnValueMappings = new Dictionary<int, Dictionary<object, object>>();
			}

			this.columnValueMappings = columnValueMappings;
		}

		/// <summary>
		/// Add a value mapping for a certain column and certain values, which will automatically be assigned to numbers (respective to their order in the list). 
		/// </summary>
		/// <param name="column">The column to add the value mapping to.</param>
		/// <param name="objects">The values to map.</param>
		/// <returns>This record extractor (for convenience).</returns>
		public CSVRecordExtractor AddAutoValueMapping(int column, params object[] objects)
		{
			return AddDirectValueMapping(column, ArrayUtils.MapToOrder(objects));
		}

		/// <summary>
		/// Add a value mapping for a certain column and certain key value pairs. Each key will be replaced with its value during extraction. 
		/// </summary>
		/// <param name="column">The column to add the value mapping to.</param>
		/// <param name="objects">The values to map.</param>
		/// <returns>This record extractor (for convenience).</returns>
		public CSVRecordExtractor AddDirectValueMapping(int column, Dictionary<object, object> mapping)
		{
			this.columnValueMappings.Add(column, mapping);

			return this;
		}

		public void Prepare()
		{
			Reader.Prepare();
		}

		public Dictionary<string, INDArray> Extract(int numberOfRecords, IComputationHandler handler)
		{
			if (Reader == null)
			{
				throw new InvalidOperationException("Cannot extract from record extractor before attaching a reader (reader was null).");
			}

			string[][] lineParts = Reader.Read<string[][]>(numberOfRecords);

			int readNumberOfRecords = lineParts.Length;

			logger.Info($"Extracting {readNumberOfRecords} records from reader {Reader} (requested: {numberOfRecords}).");

			Dictionary<string, INDArray> namedArrays = new Dictionary<string, INDArray>();

			foreach (string name in namedColumnIndexMappings.Keys)
			{
				IList<int> mappings = namedColumnIndexMappings[name];
				INDArray array = handler.Create(readNumberOfRecords, 1, mappings.Count);
				TypeConverter converter = TypeDescriptor.GetConverter(typeof(double));

				for (int i = 0; i < readNumberOfRecords; i++)
				{ 
					for (int y = 0; y < mappings.Count; y++)
					{
						int column = mappings[y];
						string value = lineParts[i][column];

						try
						{
							if (columnValueMappings.ContainsKey(column) && columnValueMappings[column].ContainsKey(value))
							{
								array.SetValue(columnValueMappings[column][value], i, 0, y);
							}
							else
							{
								array.SetValue(converter.ConvertFromString(value), i, 0, y);
							}
						}
						catch (NotSupportedException)
						{
							throw new FormatException($"Cannot convert value \"{value}\" of type {value.GetType()} to double for further processing (are you missing a column value mapping?).");
						}
					}
				}

				namedArrays.Add(name, array);
			}

			logger.Info($"Done extracting {readNumberOfRecords} records from reader {Reader} (requested: {numberOfRecords}).");

			return namedArrays;
		}

		public IRecordPreprocessor Preprocess(params IRecordPreprocessor[] preprocessors)
		{
			if (preprocessors.Length == 0)
			{
				throw new ArgumentException("Cannot add an empty array of preprocessors to an extractor.");
			}

			IRecordExtractor lastPreprocessor = this;

			foreach (IRecordPreprocessor preprocessor in preprocessors)
			{
				lastPreprocessor = lastPreprocessor.Preprocess(preprocessor);
			}

			return (IRecordPreprocessor) lastPreprocessor;
		}
	}
}