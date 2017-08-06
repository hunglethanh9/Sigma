﻿/* 
MIT License

Copyright (c) 2016-2017 Florian Cäsar, Michael Plainer

For full license see LICENSE in the root directory of this project. 
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DiffSharp.Backend;
using ManagedCuda;
using ManagedCuda.BasicTypes;
using ManagedCuda.CudaBlas;
using ManagedCuda.VectorTypes;
using Microsoft.FSharp.Core;
using Sigma.Core.Handlers.Backends.SigmaDiff.NativeCpu;
using Sigma.Core.Utils;
using static DiffSharp.Util;

namespace Sigma.Core.Handlers.Backends.SigmaDiff.NativeGpu
{
	public class CudaFloat32BackendHandle : DiffSharpBackendHandle<float>
	{
		internal CudaBlas CudaBlasHandle;
		internal readonly CudaContext CudaContext;
		internal ConditionalWeakTable<object, CudaDeviceVariable<float>> _allocatedDeviceBuffers;

		private const int ThreadsPerBlock = 256;
		private CUmodule _kernelModule;
		private IDictionary<string, CudaKernel> _loadedKernels;

		public CudaFloat32BackendHandle(int deviceId, long backendTag) : base(backendTag)
		{
			CudaContext = new CudaContext(deviceId);

			_kernelModule = CudaContext.LoadModulePTX("Dependencies/sigmakernels.ptx");
			_loadedKernels = LoadKernels(_kernelModule);

			_allocatedDeviceBuffers = new ConditionalWeakTable<object, CudaDeviceVariable<float>>();

			BindToContext();
		}

		private IDictionary<string, CudaKernel> LoadKernels(CUmodule kernelModule)
		{
			IDictionary<string, CudaKernel> loadedKernels = new Dictionary<string, CudaKernel>();

			loadedKernels.Add("Sub_V_S", new CudaKernel("_Z7Sub_V_SPffi", kernelModule, CudaContext));
			loadedKernels.Add("Sub_S_V", new CudaKernel("_Z7Sub_S_VfPfi", kernelModule, CudaContext));
			loadedKernels.Add("Add_V_S", new CudaKernel("_Z7Add_V_SPffi", kernelModule, CudaContext));
			loadedKernels.Add("Add_V_V", new CudaKernel("_Z7Add_V_VPKfiPfii", kernelModule, CudaContext));
			loadedKernels.Add("Mul_Had_V_V", new CudaKernel("_Z11Mul_Had_V_VPKfPfi", kernelModule, CudaContext));
			loadedKernels.Add("Div_S_V", new CudaKernel("_Z7Div_S_VfPfi", kernelModule, CudaContext));
			loadedKernels.Add("Div_V_V", new CudaKernel("_Z7Div_V_VPKfPfi", kernelModule, CudaContext));
			loadedKernels.Add("Exp_V", new CudaKernel("_Z5Exp_VPfi", kernelModule, CudaContext));
			loadedKernels.Add("Log_V", new CudaKernel("_Z5Log_VPfi", kernelModule, CudaContext));
			loadedKernels.Add("Sqrt_V", new CudaKernel("_Z6Sqrt_VPfi", kernelModule, CudaContext));
			loadedKernels.Add("Sign_V", new CudaKernel("_Z6Sign_VPfi", kernelModule, CudaContext));
			loadedKernels.Add("Rel_V", new CudaKernel("_Z5Rel_VPfi", kernelModule, CudaContext));
			loadedKernels.Add("Sigmoid_V", new CudaKernel("_Z9Sigmoid_VPfi", kernelModule, CudaContext));
			loadedKernels.Add("Sum_V", new CudaKernel("_Z5Sum_VPKfPfi", kernelModule, CudaContext));
			loadedKernels.Add("Softmax_Rowwise_M", new CudaKernel("_Z17Softmax_Rowwise_MPfS_S_S_iiii", kernelModule, CudaContext));
			loadedKernels.Add("Softmax_Rowwise_M_Backward", new CudaKernel("_Z26Softmax_Rowwise_M_BackwardPKfS0_S0_S0_S0_S0_Pfiiii", kernelModule, CudaContext));

			return loadedKernels;
		}

		private void RunKernel(string kernelName, int elementCount, params object[] kernelParameters)
		{
			RunKernel(kernelName, elementCount, 0, kernelParameters);
		}

		private void RunKernel(string kernelName, int elementCount, uint sharedMemoryBytes, params object[] kernelParameters)
		{
			if (!_loadedKernels.ContainsKey(kernelName))
			{
				throw new InvalidOperationException($"Unable to run kernel, kernel with name {kernelName} is not loaded.");
			}

			// TODO optimise kernel runs using async CUDA streams (requiring less synchronisation), probably need to safe guard for that in CUDA sigma buffers (maybe not due to implicit sync)

			CudaKernel kernel = _loadedKernels[kernelName];

			kernel.BlockDimensions = ThreadsPerBlock;
			kernel.GridDimensions = (elementCount + ThreadsPerBlock - 1) / ThreadsPerBlock;
			kernel.DynamicSharedMemory = sharedMemoryBytes;

			kernel.Run(kernelParameters);
		}

		internal void BindToContext()
		{
			CudaContext.SetCurrent();
			CudaBlasHandle = new CudaBlas();
		}

		/// <summary>
		/// Allocate a CUDA buffer on the used device for a certain host array.
		/// </summary>
		/// <typeparam name="T">The buffer type (only float32 supported here).</typeparam>
		/// <param name="hostData">The host version this data.</param>
		/// <param name="cudaLengthBytes">The length in bytes as a SizeT struct (if allocation is required).</param>
		/// <returns>A CUDA buffer corresponding to the host array of the required size (cached if already exists, otherwise newly allocated).</returns>
		internal CudaDeviceVariable<T> AllocateDeviceBuffer<T>(T[] hostData, SizeT cudaLengthBytes) where T : struct
		{
			// TODO this casting and type checking is absolutely horribly, need to improve the way the data buffer accesses this so that it can be either truly dynamic or fixed type
			if (typeof(T) != typeof(float)) throw new InvalidOperationException($"{nameof(CudaFloat32BackendHandle)} can only allocate float32 device buffers, given type {typeof(T)} is not valid.");

			// The caching here works because I'm essentially tagging along with the system memory caching done in DiffSharpBackendHandle<T>.
			// Basically, the idea is that every host array has a corresponding device buffer, and because the host arrays are already reused as necessary,
			//  the device buffers are too as they are weakly associated with the host arrays in a weak table. This also automatically takes care of "freeing" device buffers.
			CudaDeviceVariable<float> deviceBuffer;
			if (_allocatedDeviceBuffers.TryGetValue(hostData, out deviceBuffer))
			{
				// TODO temp fix to make sure the returned data is of the right size, maybe manage offset / length separately for host data, unfortunately cuda doesn't support buffer overlap					 
				if (deviceBuffer.SizeInBytes == cudaLengthBytes)
				{
					return (CudaDeviceVariable<T>)(object)deviceBuffer;
				}
				else
				{
					_allocatedDeviceBuffers.Remove(hostData);
				}
			}

			deviceBuffer = new CudaDeviceVariable<float>(CudaContext.AllocateMemory(cudaLengthBytes), true, cudaLengthBytes);

			_allocatedDeviceBuffers.Add(hostData, deviceBuffer);

			return (CudaDeviceVariable<T>)(object)deviceBuffer;
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> CreateDataBuffer(float[] values)
		{
			return new CudaSigmaDiffDataBuffer<float>(values, BackendTag, CudaContext);
		}

		private CudaSigmaDiffDataBuffer<float> _InternalInternalise(ISigmaDiffDataBuffer<float> value)
		{
			return (CudaSigmaDiffDataBuffer<float>)value;
		}

		private CudaSigmaDiffDataBuffer<float> _InternalInternalise(ShapedDataBufferView<float> value)
		{
			return (CudaSigmaDiffDataBuffer<float>)value.DataBuffer;
		}

		internal class CustomOpHandle
		{
			internal CustomOpType Type { get; }
			private IDictionary<string, object> AdditionalInfo { get; }

			internal CustomOpHandle(CustomOpType type)
			{
				Type = type;
				AdditionalInfo = new Dictionary<string, object>();
			}

			internal void AttachInfo(string identifier, object obj)
			{
				AdditionalInfo.Add(identifier, obj);
			}

			internal T GetInfo<T>(string identifier)
			{
				return (T) AdditionalInfo[identifier];
			}
		}

		internal enum CustomOpType
		{
			RowWiseSoftmax
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> CustomOp_DM_Forward(ShapedDataBufferView<float> a, object customInfo)
		{
			if (!(customInfo is CustomOpHandle))
			{
				throw new InvalidOperationException($"Cannot invoke {nameof(CustomOp_DM_Forward)} with invalid custom info of type {customInfo.GetType()} (must be of type {nameof(CustomOpHandle)}).");
			}

			CustomOpHandle op = (CustomOpHandle) customInfo;

			if (!Enum.IsDefined(typeof(CustomOpType), op.Type))
			{
				throw new NotImplementedException($"Custom op {op} is not supported in {nameof(CustomOp_DM_Forward)}.");
			}

			int len = a.Length;
			a = a.DeepCopy();

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);

			if (op.Type == CustomOpType.RowWiseSoftmax)
			{
				int colsNextPowerOf2 = ArrayUtils.NextHighestPowerOf2(a.Cols);
				CudaSigmaDiffDataBuffer<float> maxBuffer = _InternalInternalise(CreateDataBuffer(CreateUninitialisedArray(a.Rows)));
				CudaSigmaDiffDataBuffer<float> maxIndicesBuffer = _InternalInternalise(CreateDataBuffer(CreateUninitialisedArray(a.Rows)));
				CudaSigmaDiffDataBuffer<float> sumBuffer = _InternalInternalise(CreateDataBuffer(CreateUninitialisedArray(a.Rows)));

				RunKernel("Softmax_Rowwise_M", len, ThreadsPerBlock * sizeof(float) * 2, aData.GetContextPointer(), maxBuffer.GetContextPointer(), 
					maxIndicesBuffer.GetContextPointer(), sumBuffer.GetContextPointer(), a.Rows, a.Cols, colsNextPowerOf2, len);

				maxBuffer.FlagDeviceModified();
				maxIndicesBuffer.FlagDeviceModified();
				sumBuffer.FlagDeviceModified();

				op.AttachInfo("prevMaxs", maxBuffer);
				op.AttachInfo("prevMaxIndices", maxBuffer);
				op.AttachInfo("prevSums", sumBuffer);
			}

			aData.FlagDeviceModified();

			return a;
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> CustomOp_DM_Backward(ShapedDataBufferView<float> origin, 
			ShapedDataBufferView<float> adjoint, ShapedDataBufferView<float> primal, object customInfo)
		{
			if (!(customInfo is CustomOpHandle))
			{
				throw new InvalidOperationException($"Cannot invoke {nameof(CustomOp_DM_Forward)} with invalid custom info of type {customInfo.GetType()} (must be of type {nameof(CustomOpHandle)}).");
			}

			CustomOpHandle op = (CustomOpHandle)customInfo;

			if (!Enum.IsDefined(typeof(CustomOpType), op.Type))
			{
				throw new NotImplementedException($"Custom op {op} is not supported in {nameof(CustomOp_DM_Backward)}.");
			}

			CudaSigmaDiffDataBuffer<float> rData = _InternalInternalise(CreateDataBuffer(CreateUninitialisedArray(origin.Length)));
			int len = (int) rData.Length;

			if (op.Type == CustomOpType.RowWiseSoftmax)
			{
				CudaSigmaDiffDataBuffer<float> originData = _InternalInternalise(origin);
				CudaSigmaDiffDataBuffer<float> adjointData = _InternalInternalise(adjoint);
				CudaSigmaDiffDataBuffer<float> primalData = _InternalInternalise(primal);
				CudaSigmaDiffDataBuffer<float> maxBuffer = op.GetInfo<CudaSigmaDiffDataBuffer<float>>("prevMaxs");
				CudaSigmaDiffDataBuffer<float> maxIndicesBuffer = op.GetInfo<CudaSigmaDiffDataBuffer<float>>("prevMaxIndices"); ;
				CudaSigmaDiffDataBuffer<float> sumBuffer = op.GetInfo<CudaSigmaDiffDataBuffer<float>>("prevSums");

				// TODO refactor x.GetContextPointer() into single method for convenience
				RunKernel("Softmax_Rowwise_M_Backward", len, ThreadsPerBlock * sizeof(float) * 5, originData.GetContextPointer(), adjointData.GetContextPointer(),
					primalData.GetContextPointer(), maxBuffer.GetContextPointer(), maxIndicesBuffer.GetContextPointer(), 
					sumBuffer.GetContextPointer(), rData.GetContextPointer(), origin.Rows, origin.Cols, ArrayUtils.NextHighestPowerOf2(origin.Cols), len);
			}

			rData.FlagDeviceModified();

			return new ShapedDataBufferView<float>(rData, origin.Rows, origin.Cols);
		}

		/// <inheritdoc />
		public override float Mul_Dot_V_V(ISigmaDiffDataBuffer<float> a, ISigmaDiffDataBuffer<float> n)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override float L1Norm_V(ISigmaDiffDataBuffer<float> value)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override float L2Norm_V(ISigmaDiffDataBuffer<float> value)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override float SupNorm_V(ISigmaDiffDataBuffer<float> value)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override unsafe float Sum_V(ISigmaDiffDataBuffer<float> a)
		{
			int len = a.Length, lenPartials = (len + ThreadsPerBlock - 1) / ThreadsPerBlock;

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
			CudaSigmaDiffDataBuffer<float> partialSums = (CudaSigmaDiffDataBuffer<float>) CreateDataBuffer(CreateUninitialisedArray(lenPartials));

			RunKernel("Sum_V", len, (uint) (ThreadsPerBlock * sizeof(float)), aData.GetContextPointer(), partialSums.GetContextPointer(), len);
			RunKernel("Sum_V", lenPartials, (uint) (ThreadsPerBlock * sizeof(float)), partialSums.GetContextPointer(), partialSums.GetContextPointer(), lenPartials);

			partialSums.FlagDeviceModified();

			return partialSums.Data[0]; // TODO this is sub-optimal as we lose the advantages of having asynchronous GPU execution when explictly awaiting a result on host (e.g. the sum)
		}

		/// <inheritdoc />
		public override float Sum_M(ISigmaDiffDataBuffer<float> value)
		{
			return Sum_V(value);
		}

		/// <inheritdoc />
		public override unsafe int MaxIndex_V(ISigmaDiffDataBuffer<float> value)
		{
			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(value);

			// TODO optimise using custom kernel for relative minimum value (not minimum magnitude like the (cu)blas implementation)
			int maxIndex = 0, len = (int)aData.Length;
			float maxValue = float.MinValue;

			fixed (float* aref = &aData.Data[aData.Offset])
			{
				for (int k = 0; k < len; k++)
				{
					if (aref[k] > maxValue)
					{
						maxValue = aref[k];
						maxIndex = k;
					}
				}
			}

			return maxIndex;
		}

		/// <inheritdoc />
		public override unsafe int MinIndex_V(ISigmaDiffDataBuffer<float> value)
		{
			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(value);

			// TODO optimise using custom kernel for relative minimum value (not minimum magnitude like the (cu)blas implementation)
			int minIndex = 0, len = (int)aData.Length;
			float minValue = float.MaxValue;

			fixed (float* aref = &aData.Data[aData.Offset])
			{
				for (int k = 0; k < len; k++)
				{
					if (aref[k] < minValue)
					{
						minValue = aref[k];
						minIndex = k;
					}
				}
			}

			return minIndex;
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Add_V_V(ISigmaDiffDataBuffer<float> a, ISigmaDiffDataBuffer<float> b)
		{
			if (a.Length == 0) return b.DeepCopy();
			if (b.Length == 0) return a.DeepCopy();

			b = b.DeepCopy();

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
			CudaSigmaDiffDataBuffer<float> bData = _InternalInternalise(b);

			float alpha = 1.0f;

			CudaBlasHandle.Axpy(alpha, aData.GetContextBuffer(), 1, bData.GetContextBuffer(), 1);

			bData.FlagDeviceModified();

			return b;
		}

		/// <inheritdoc />
		public override unsafe ISigmaDiffDataBuffer<float> Add_V_V_InPlace(ISigmaDiffDataBuffer<float> a, int aOffset, ISigmaDiffDataBuffer<float> b, int bOffset, int len)
		{
			if (len == 0)
			{
				return b;
			}

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
			CudaSigmaDiffDataBuffer<float> bData = _InternalInternalise(b);

			RunKernel("Add_V_V", len, aData.GetContextPointer(), aOffset, bData.GetContextPointer(), bOffset, len);

			bData.FlagDeviceModified();

			return b;
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Add_S_V(float a, ISigmaDiffDataBuffer<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Sub_V_V(ISigmaDiffDataBuffer<float> a, ISigmaDiffDataBuffer<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Sub_S_V(float a, ISigmaDiffDataBuffer<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Sub_V_S(ISigmaDiffDataBuffer<float> a, float b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Mul_S_V(float a, ISigmaDiffDataBuffer<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Mul_M_V(ShapedDataBufferView<float> a, ISigmaDiffDataBuffer<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Mul_M_V_Add_V(ShapedDataBufferView<float> a, ISigmaDiffDataBuffer<float> b, ISigmaDiffDataBuffer<float> obj2)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Mul_V_M(ISigmaDiffDataBuffer<float> a, ShapedDataBufferView<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override FSharpOption<ISigmaDiffDataBuffer<float>> Solve_M_V(ShapedDataBufferView<float> a, ISigmaDiffDataBuffer<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override FSharpOption<ISigmaDiffDataBuffer<float>> SolveSymmetric_M_V(ShapedDataBufferView<float> a, ISigmaDiffDataBuffer<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Diagonal_M(ShapedDataBufferView<float> a)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Map_F_V(MapOp mapOp, FSharpFunc<float, float> function, ISigmaDiffDataBuffer<float> value)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Map_F_S_V(float other, MapOp mapOp, FSharpFunc<float, float> function, ISigmaDiffDataBuffer<float> value)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> Map2_F_V_V(MapOp mapOp, FSharpFunc<float, FSharpFunc<float, float>> function, ISigmaDiffDataBuffer<float> a, ISigmaDiffDataBuffer<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Map_F_M(MapOp mapOp, FSharpFunc<float, float> f, ShapedDataBufferView<float> a)
		{
			if (a.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(new float[0]), 0L, 0L);
			}

			a = a.DeepCopy();

			if (!_InternalOptimisedMap_F_M(mapOp, a))
			{
				int upper = a.DataBuffer.Offset + a.DataBuffer.Length;
				float[] data = a.DataBuffer.Data;

				for (int i = a.DataBuffer.Offset; i < upper; i++)
				{
					data[i] = f.Invoke(data[i]);
				}
			}

			return a;
		}

		private bool _InternalOptimisedMap_F_M(MapOp mapOp, ShapedDataBufferView<float> a)
		{
			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
			int len = (int) aData.Length;

			if (mapOp.IsExp)
			{
				RunKernel("Exp_V", len, aData.GetContextPointer(), len);

				return true;
			}
			else if (mapOp.IsSqrt)
			{
				RunKernel("Sqrt_V", len, aData.GetContextPointer(), len);

				return true;
			}
			else if (mapOp.IsSign)
			{
				RunKernel("Sign_V", len, aData.GetContextPointer(), len);

				return true;
			}
			else if (mapOp.IsReL)
			{
				RunKernel("Rel_V", len, aData.GetContextPointer(), len);

				return true;
			}
			else if (mapOp.IsLog)
			{
				RunKernel("Log_V", len, aData.GetContextPointer(), len);

				return true;
			}
			else if (mapOp.IsSigmoid)
			{
				RunKernel("Sigmoid_V", len, aData.GetContextPointer(), len);

				return true;
			}

			return false;
		}


		/// <inheritdoc />
		public override ShapedDataBufferView<float> Map_F_S_M(float other, MapOp mapOp, FSharpFunc<float, float> function, ShapedDataBufferView<float> value)
		{
			if (_InternalOptimisedMapOp_F_S_M(other, mapOp, ref value))
			{
				return value;
			}

			return Map_F_M(mapOp, function, value);
		}

		private bool _InternalOptimisedMapOp_F_S_M(float other, MapOp mapOp, ref ShapedDataBufferView<float> a)
		{
			int len = a.Length;

			if (mapOp.IsDiv)
			{
				a = a.DeepCopy();
				CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);

				RunKernel("Div_S_V", len, other, aData.GetContextPointer(), len);

				return true;
			}

			return false;
		}

		/// <inheritdoc />
		public override unsafe ShapedDataBufferView<float> Map2_F_M_M(MapOp mapOp, FSharpFunc<float, FSharpFunc<float, float>> f, ShapedDataBufferView<float> a, ShapedDataBufferView<float> b)
		{
			if (a.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(new float[0]), 0L, 0L);
			}
			if (b.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(new float[0]), 0L, 0L);
			}

			if (_InternalOptimisedMapOp_F_M_M(mapOp, a, ref b))
			{
				return b;
			}

			b = b.DeepCopy();

			float[] aData = a.DataBuffer.Data, bData = b.DataBuffer.Data;
			int aOffset = a.DataBuffer.Offset, bOffset = b.DataBuffer.Offset;

			fixed (float* aref = &aData[aOffset])
			fixed (float* bref = &bData[bOffset])
			{
				for (int i = 0; i < a.Length; i++)
				{
					bref[i] = f.Invoke(aref[i]).Invoke(bData[i]);
				}
			}

			return b;
		}

		private bool _InternalOptimisedMapOp_F_M_M(MapOp mapOp, ShapedDataBufferView<float> a, ref ShapedDataBufferView<float> b)
		{
			int len = b.Length;

			b = b.DeepCopy();

			if (mapOp.IsDiv)
			{
				CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
				CudaSigmaDiffDataBuffer<float> bData = _InternalInternalise(b);

				RunKernel("Div_V_V", len, aData.GetContextPointer(), bData.GetContextPointer(), len);

				return true;
			}

			return false;
		}

		/// <inheritdoc />
		public override ISigmaDiffDataBuffer<float> ReshapeCopy_MRows_V(ShapedDataBufferView<float> value)
		{
			if (value.Length == 0)
			{
				return CreateDataBuffer(new float[0]);
			}

			return value.DataBuffer.DeepCopy();
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Mul_Out_V_V(ISigmaDiffDataBuffer<float> a, ISigmaDiffDataBuffer<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Add_M_M(ShapedDataBufferView<float> a, ShapedDataBufferView<float> b)
		{
			if (a.Length == 0) return b.DeepCopy();
			if (b.Length == 0) return a.DeepCopy();

			b = b.DeepCopy();

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
			CudaSigmaDiffDataBuffer<float> bData = _InternalInternalise(b);

			float alpha = 1.0f;

			CudaBlasHandle.Axpy(alpha, aData.GetContextBuffer(), 1, bData.GetContextBuffer(), 1);

			bData.FlagDeviceModified();

			return b;
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Add_M_M_InPlace(ShapedDataBufferView<float> a, ShapedDataBufferView<float> b)
		{
			if (a.Length == 0) return b;
			if (b.Length == 0) return a;

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
			CudaSigmaDiffDataBuffer<float> bData = _InternalInternalise(b);

			float alpha = 1.0f;

			CudaBlasHandle.Axpy(alpha, aData.GetContextBuffer(), 1, bData.GetContextBuffer(), 1);

			bData.FlagDeviceModified();

			return b;
		}

		/// <inheritdoc />
		public override unsafe ShapedDataBufferView<float> Add_S_M(float other, ShapedDataBufferView<float> a)
		{
			int len = a.Length;

			a = a.DeepCopy();

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);

			RunKernel("Add_V_S", len, aData.GetContextPointer(), other, len);

			aData.FlagDeviceModified();

			return a;
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Add_V_MCols(ISigmaDiffDataBuffer<float> a, ShapedDataBufferView<float> b)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Sub_M_M(ShapedDataBufferView<float> a, ShapedDataBufferView<float> b)
		{
			if (a.Length == 0) return b.DeepCopy();
			if (b.Length == 0) return a.DeepCopy();

			a = a.DeepCopy();

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
			CudaSigmaDiffDataBuffer<float> bData = _InternalInternalise(b);

			float alpha = -1.0f;

			CudaBlasHandle.Axpy(alpha, bData.GetContextBuffer(), 1, aData.GetContextBuffer(), 1);

			aData.FlagDeviceModified();

			return a;
		}

		/// <inheritdoc />
		public override unsafe ShapedDataBufferView<float> Sub_M_S(ShapedDataBufferView<float> a, float b)
		{
			if (a.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(new float[0]), 0L, 0L);
			}

			a = a.DeepCopy();
			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);

			int len = (int)aData.Length;

			RunKernel("Sub_V_S", len, aData.GetContextPointer(), b, len);

			aData.FlagDeviceModified();

			return a;
		}

		/// <inheritdoc />
		public override unsafe ShapedDataBufferView<float> Sub_S_M(float other, ShapedDataBufferView<float> a)
		{
			if (a.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(new float[0]), 0L, 0L);
			}

			a = a.DeepCopy();
			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);

			int len = (int)aData.Length;

			RunKernel("Sub_S_V", len, other, aData.GetContextPointer(), len);

			aData.FlagDeviceModified();

			return a;
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Mul_M_M(ShapedDataBufferView<float> a, ShapedDataBufferView<float> b)
		{
			if (a.Length * b.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(new float[0]), 0L, 0L);
			}

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
			CudaSigmaDiffDataBuffer<float> bData = _InternalInternalise(b);
			CudaSigmaDiffDataBuffer<float> zData = (CudaSigmaDiffDataBuffer<float>)CreateDataBuffer(CreateUninitialisedArray(a.Rows * b.Cols));

			float alpha = 1.0f, beta = 0.0f;
			int m = a.Rows, n = b.Cols, k = b.Rows;

			CudaBlasHandle.Gemm(Operation.NonTranspose, Operation.NonTranspose, n, m, k, alpha, bData.GetContextBuffer(), n,
				aData.GetContextBuffer(), k, beta, zData.GetContextBuffer(), n);

			zData.FlagDeviceModified();

			return new ShapedDataBufferView<float>(zData, a.Rows, b.Cols);
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Mul_S_M(float a, ShapedDataBufferView<float> b)
		{
			if (b.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(new float[0]), 0L, 0L);
			}

			b = b.DeepCopy();

			CudaSigmaDiffDataBuffer<float> bData = _InternalInternalise(b);

			CudaBlasHandle.Scale(a, bData.GetContextBuffer(), 1);

			bData.FlagDeviceModified();

			return b;
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Mul_M_M_Add_V_MCols(ShapedDataBufferView<float> a, ShapedDataBufferView<float> b, ISigmaDiffDataBuffer<float> obj2)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override unsafe ShapedDataBufferView<float> Mul_Had_M_M(ShapedDataBufferView<float> a, ShapedDataBufferView<float> b)
		{
			if (a.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(CreateZeroArray(b.Length)), b.Shape);
			}
			if (b.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(CreateZeroArray(a.Length)), a.Shape);
			}

			int len = Math.Min(a.Length, b.Length);
			b = b.DeepCopy();

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
			CudaSigmaDiffDataBuffer<float> bData = _InternalInternalise(b);

			RunKernel("Mul_Had_V_V", len, aData.GetContextPointer(), bData.GetContextPointer(), len);

			bData.FlagDeviceModified();
			
			return b;
		}

		/// <inheritdoc />
		public override FSharpOption<ShapedDataBufferView<float>> Inverse_M(ShapedDataBufferView<float> a)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override FSharpOption<float> Det_M(ShapedDataBufferView<float> a)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Transpose_M(ShapedDataBufferView<float> a)
		{
			if (a.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(new float[0]), 0L, 0L);
			}

			ShapedDataBufferView<float> transposed = a.DeepCopy();

			for (int i = 0; i < transposed.Shape.Length; i++)
			{
				transposed.Shape[i] = a.Shape[a.Shape.Length - 1 - i];
			}

			CudaSigmaDiffDataBuffer<float> aData = _InternalInternalise(a);
			CudaSigmaDiffDataBuffer<float> tData = _InternalInternalise(transposed);

			float alpha = 1.0f, beta = 0.0f;
			int m = a.Rows, n = a.Cols;

			CudaBlasHandle.Geam(Operation.Transpose, Operation.NonTranspose, m, n, alpha, aData.GetContextBuffer(), n, tData.GetContextBuffer(), m, beta, tData.GetContextBuffer(), m);

			tData.FlagDeviceModified();

			return transposed;
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Permute_M(ShapedDataBufferView<float> array, int[] rearrangedDimensions)
		{
			throw new NotImplementedException();
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> Reshape_M(ShapedDataBufferView<float> array, long[] newShape)
		{
			ShapedDataBufferView<float> reshaped = new ShapedDataBufferView<float>(array.DataBuffer, newShape);

			return reshaped;
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> ReshapeCopy_V_MRows(int rows, ISigmaDiffDataBuffer<float> value)
		{
			if (value.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(new float[0]), 0L, 0L);
			}

			int n = value.Length / rows;

			return new ShapedDataBufferView<float>(value.DeepCopy(), rows, n);
		}

		/// <inheritdoc />
		public override unsafe ShapedDataBufferView<float> RepeatReshapeCopy_V_MRows(int rows, ISigmaDiffDataBuffer<float> row)
		{
			// TODO optimise with CUDA in device copies (if applicable)
			if (row.Length == 0)
			{
				return new ShapedDataBufferView<float>(CreateDataBuffer(new float[0]), 0L, 0L);
			}

			int rowLength = row.Length;
			float[] result = CreateUninitialisedArray(rows * rowLength);
			CudaSigmaDiffDataBuffer<float> rowSubData = _InternalInternalise(row);
			CudaSigmaDiffDataBuffer<float> resultData = (CudaSigmaDiffDataBuffer<float>)CreateDataBuffer(result);

			if (!rowSubData.IsInitialisedInContext())
			{
				float[] rowData = row.Data;
				int sourceOffset = row.Offset;
				int destinationOffset = 0;

				for (int i = 0; i < rows; i++)
				{
					Buffer.BlockCopy(rowData, sourceOffset * sizeof(float), result, destinationOffset * sizeof(float), rowLength * sizeof(float));

					destinationOffset += rowLength;
				}
			}
			else
			{
				resultData.InitialiseCudaBuffer(copyHostToDevice: false);

				CudaDeviceVariable<float> subBuffer = rowSubData.GetContextBuffer();
				CudaDeviceVariable<float> resultBuffer = resultData.GetContextBuffer();
				SizeT cudaSourceOffset = new SizeT(0);
				SizeT cudaDestOffset = new SizeT(0);

				for (int i = 0; i < rows; i++)
				{
					resultBuffer.CopyToDevice(subBuffer, cudaSourceOffset, cudaDestOffset, subBuffer.SizeInBytes);

					cudaDestOffset += subBuffer.SizeInBytes;
				}

				resultData.FlagDeviceModified();
			}

			return new ShapedDataBufferView<float>(resultData, rows, rowLength);
		}

		/// <inheritdoc />
		public override ShapedDataBufferView<float> RepeatReshapeCopy_V_MCols(int cols, ISigmaDiffDataBuffer<float> value)
		{
			throw new NotImplementedException();
		}
	}
}
