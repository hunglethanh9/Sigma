﻿/* 
MIT License

Copyright (c) 2016-2017 Florian Cäsar, Michael Plainer

For full license see LICENSE in the root directory of this project. 
*/

using System;
using DiffSharp;
using DiffSharp.Backend;
using ManagedCuda;
using ManagedCuda.BasicTypes;
using ManagedCuda.CudaBlas;
using Sigma.Core.Data;
using Sigma.Core.Persistence;

namespace Sigma.Core.Handlers.Backends.SigmaDiff.NativeGpu
{
	[Serializable]
	public class CudaSigmaDiffDataBuffer<T> : SigmaDiffDataBuffer<T>, ISerialisationNotifier where T : struct
	{
		[NonSerialized]
		internal CudaContext CudaContext;

		// TODO implement more intelligent host <-> device synchronisation by flagging these whenever meaningful host read / device write access occurs
		private bool _flagDeviceModified;
		private bool _flagHostModified;

		[NonSerialized]
		private bool _initialisedInContext;

		private int _cudaContextDeviceId;

		[NonSerialized]
		private CudaDeviceVariable<T> _cudaBuffer;
		[NonSerialized]
		private SizeT _cudaZero;
		[NonSerialized]
		private SizeT _cudaOffsetBytes, _cudaLengthBytes;

		/// <inheritdoc />
		public CudaSigmaDiffDataBuffer(IDataBuffer<T> underlyingBuffer, long offset, long length, long backendTag, CudaContext cudaContext) : base(underlyingBuffer, offset, length, backendTag)
		{
			PrepareCudaBuffer(cudaContext, Data, Offset, Length);
		}

		/// <inheritdoc />
		public CudaSigmaDiffDataBuffer(T[] data, long backendTag, CudaContext cudaContext, IDataType underlyingType = null) : base(data, backendTag, underlyingType)
		{
			PrepareCudaBuffer(cudaContext, Data, Offset, Length);
		}

		/// <inheritdoc />
		public CudaSigmaDiffDataBuffer(T[] data, long offset, long length, long backendTag, CudaContext cudaContext, IDataType underlyingType = null) : base(data, offset, length, backendTag, underlyingType)
		{
			PrepareCudaBuffer(cudaContext, Data, Offset, Length);
		}

		/// <inheritdoc />
		public CudaSigmaDiffDataBuffer(long length, long backendTag, CudaContext cudaContext, IDataType underlyingType = null) : base(length, backendTag, underlyingType)
		{
			PrepareCudaBuffer(cudaContext, Data, Offset, Length);
		}

		/// <inheritdoc />
		public CudaSigmaDiffDataBuffer(DataBuffer<T> other, long backendTag, CudaContext cudaContext) : base(other, backendTag)
		{
			PrepareCudaBuffer(cudaContext, Data, Offset, Length);
		}

		/// <summary>
		/// Shallow copy constructor with the same system- and device memory buffers.
		/// </summary>
		/// <param name="other"></param>
		internal CudaSigmaDiffDataBuffer(CudaSigmaDiffDataBuffer<T> other) : base(other, other.BackendTag)
		{
			_cudaBuffer = other._cudaBuffer;
			CudaContext = other.CudaContext;

			_cudaOffsetBytes = other._cudaOffsetBytes;
			_cudaLengthBytes = other._cudaLengthBytes;
		}

		private void PrepareCudaBuffer(CudaContext cudaContext, T[] data, long offset, long length)
		{
			CudaContext = cudaContext;

			_cudaZero = new SizeT(0);
			_cudaOffsetBytes = new SizeT(offset * Type.SizeBytes);
			_cudaLengthBytes = new SizeT(length * Type.SizeBytes);

			_cudaContextDeviceId = cudaContext.DeviceId;
		}

		/// <summary>
		/// Called before this object is serialised.
		/// </summary>
		public void OnSerialising()
		{
		}

		/// <summary>
		/// Called after this object was de-serialised. 
		/// </summary>
		public void OnDeserialised()
		{
			CudaContext restoredContext = CudaFloat32Handler.GetContextForDeviceId(_cudaContextDeviceId);

			PrepareCudaBuffer(restoredContext, Data, Offset, Length);
		}

		private void InitialiseCudaBuffer()
		{
			if (CudaContext == null) throw new InvalidOperationException($"Cannot initialise cuda buffer, cuda context is invalid (null).");

			CudaFloat32BackendHandle backendHandle = (CudaFloat32BackendHandle)SigmaDiffSharpBackendProvider.Instance.GetBackend<T>(BackendTag).BackendHandle;

			_cudaBuffer = backendHandle.AllocateDeviceBuffer(Data, _cudaLengthBytes);
			_initialisedInContext = true;

			CopyFromHostToDevice();
		}

		internal CudaDeviceVariable<T> GetContextBuffer()
		{
			if (!_initialisedInContext)
			{
				InitialiseCudaBuffer();
			}

			SynchroniseFromHostToDevice();

			return _cudaBuffer;
		}

		/// <summary>
		/// Called before any operation that reads from the local data.
		/// </summary>
		protected override void OnReadAccess()
		{
			SynchroniseFromDeviceToHost();
		}

		/// <summary>
		/// Called before any operation that writes to the local data.
		/// </summary>
		protected override void OnWriteAccess()
		{
			SynchroniseFromDeviceToHost();

			_flagHostModified = true;
		}

		/// <summary>
		/// Called before any operation that reads from and writes to the local data.
		/// </summary>
		protected override void OnReadWriteAccess()
		{
			SynchroniseFromDeviceToHost();

			_flagHostModified = true;
		}

		internal void FlagDeviceModified()
		{
			_flagDeviceModified = true;
		}

		internal void FlagHostModified()
		{
			_flagHostModified = true;
		}

		internal void SynchroniseFromHostToDevice()
		{
			if (_flagHostModified)
			{
				if (_flagDeviceModified)
				{
					throw new InvalidOperationException($"Unable to synchronise buffers from host to device, both device and host buffers are marked modified.");
				}

				CopyFromHostToDevice();

				_flagHostModified = false;
			}
		}

		internal void SynchroniseFromDeviceToHost()
		{
			if (_flagDeviceModified)
			{
				if (_flagHostModified)
				{
					throw new InvalidOperationException($"Unable to synchronise buffers from device to host, both device and host buffers are marked modified.");
				}

				CopyFromDeviceToHost();

				_flagDeviceModified = false;
			}
		}

		internal void CopyFromHostToDevice()
		{
			if (!_initialisedInContext)
			{
				InitialiseCudaBuffer();
			}

			_cudaBuffer.CopyToDevice(_data, _cudaOffsetBytes, _cudaZero, _cudaLengthBytes);
		}

		internal void CopyFromDeviceToHost()
		{
			if (!_initialisedInContext)
			{
				InitialiseCudaBuffer();
			}

			_cudaBuffer.CopyToHost(_data, _cudaZero, _cudaOffsetBytes, _cudaLengthBytes);
		}

		/// <inheritdoc />
		protected override Util.ISigmaDiffDataBuffer<T> _InternalDeepCopy(T[] copyData)
		{
			return new CudaSigmaDiffDataBuffer<T>(copyData, 0L, Length, BackendTag, CudaContext, Type);
		}

		/// <inheritdoc />
		protected override Util.ISigmaDiffDataBuffer<T> _InternalShallowCopy()
		{
			return new CudaSigmaDiffDataBuffer<T>(this);
		}

		/// <inheritdoc />
		public override Util.ISigmaDiffDataBuffer<T> GetStackedValues(int totalRows, int totalCols, int rowStart, int rowFinish, int colStart, int colFinish)
		{
			int colLength = colFinish - colStart + 1;
			int newSize = (rowFinish - rowStart + 1) * colLength;
			Backend<T> backendHandle = SigmaDiffSharpBackendProvider.Instance.GetBackend<T>(BackendTag).BackendHandle;
			CudaSigmaDiffDataBuffer<T> values = (CudaSigmaDiffDataBuffer<T>)backendHandle.CreateDataBuffer(backendHandle.CreateUninitialisedArray(newSize));

			for (int m = rowStart; m <= rowFinish; m++)
			{
				long sourceIndex = Offset + m * totalCols + colStart;
				long destinationIndex = (m - rowStart) * colLength;

				Buffer.BlockCopy(Data, (int)(sourceIndex * Type.SizeBytes), values.Data, (int)(destinationIndex * Type.SizeBytes), colLength * Type.SizeBytes);
			}

			if (_initialisedInContext)
			{
				values.CopyFromHostToDevice();
			}

			return values;
		}

		/// <inheritdoc />
		public override IDataBuffer<T> GetValues(long startIndex, long length)
		{
			OnReadAccess();

			return new CudaSigmaDiffDataBuffer<T>(this, startIndex, length, BackendTag, CudaContext);
		}

		/// <inheritdoc />
		public override IDataBuffer<TOther> GetValuesAs<TOther>(long startIndex, long length)
		{
			OnReadAccess();

			return new CudaSigmaDiffDataBuffer<TOther>(GetValuesArrayAs<TOther>(startIndex, length), 0L, length, BackendTag, CudaContext);
		}

		/// <summary>
		/// Called after this object was serialised.
		/// </summary>
		public void OnSerialised()
		{
		}
	}
}
