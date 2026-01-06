using LarinLive.WinAPI;
using System;
using System.Buffers;
using System.IO.Enumeration;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using static LarinLive.WinAPI.NativeMethods.ErrorCodes;
using static LarinLive.WinAPI.NativeMethods.Kernel32;
using static LarinLive.WinAPI.NativeMethods.Pdh;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Paniquer.Agent;

public abstract record class ObservableObject
{

}

public class PerfCounterMapping<T> where T : notnull, ObservableObject
{
	public required T BaseObject { get; set; }

	public string CounterPath { get; set; }
}

public record class Volume : ObservableObject
{
	public required string Name { get; init; } 

	public string? DeviceName { get; init; }

	public uint DeviceType { get; set; }

	public uint PhysicalDeviceNumber { get; set; }

	public string[] MountPoints { get; init; } = [];

	public string? FileSystemName { get; init; }

	public string? Label { get; init; }

	public bool IsReadOnly { get; init; }

	public ulong Size { get; set; }

	public string? PerfCounterInstance { get; set; }
}


public static class SpanExtensions
{
	public static Span<T> SliceFirst<T>(this Span<T> input, T splitter) where T : IEquatable<T>
	{
		var i = input.IndexOf(splitter);
		if (i > 0)
			return input[..i];
		else
			return input;
	}

	public static ReadOnlySpan<T> SliceFirst<T>(this ReadOnlySpan<T> input, T splitter) where T : IEquatable<T>
	{
		var i = input.IndexOf(splitter);
		if (i > 0)
			return input[..i];
		else
			return input;
	}
}

internal unsafe class Program
{
	static void Main(string[] args)
	{
		uint lastError;
		var volumes = new List<Volume>();
		var volumeNameLength = MAX_PATH + 1;
		var volumeName = ArrayPool<char>.Shared.Rent((int)volumeNameLength);
		try
		{
			var deviceNameLength = MAX_PATH + 1;
			var deviceName = ArrayPool<char>.Shared.Rent((int)deviceNameLength);
			try
			{
				var labelLength = MAX_PATH + 1;
				var label = ArrayPool<char>.Shared.Rent((int)labelLength);
				try
				{
					var fsNameLength = MAX_PATH + 1;
					var fsName = ArrayPool<char>.Shared.Rent((int)fsNameLength);
					try
					{
						nint hVolumeSearch;
						fixed (char* pVolumeName = volumeName)
							hVolumeSearch = FindFirstVolumeW(pVolumeName, volumeNameLength).VerifyWinapiValidHandle();
						try
						{
							while (true)
							{
								var volumeNameSpan = volumeName.AsSpan().SliceFirst('\0');
								var i = volumeNameSpan.Length;
								volumeName[i - 1] = '\0';

								fixed (char* pVolumeName = volumeNameSpan[4..], pDeviceName = deviceName)
									QueryDosDeviceW(pVolumeName, pDeviceName, deviceNameLength).VerifyWinapiNonzero();

								volumeName[i - 1] = '\\';
								var deviceNameSpan = deviceName.AsSpan().SliceFirst('\0');

								var mp = new List<string>();
								fixed (char* pVolumeName = volumeName)
								{
									var len = 0U;
									GetVolumePathNamesForVolumeNameW(pVolumeName, null, len, &len);
									lastError = GetLastError();
									if (lastError == ERROR_MORE_DATA)
									{
										var mountPoints = ArrayPool<char>.Shared.Rent((int)len);
										try
										{
											fixed (char* pMountPoints = mountPoints)
												GetVolumePathNamesForVolumeNameW(pVolumeName, pMountPoints, len, &len).VerifyWinapiNonzero();

											var mountPointsSpan = mountPoints.AsSpan();
											while (true)
											{
												i = mountPointsSpan.IndexOf('\0');
												if (i > 0)
												{
													mp.Add(new(mountPointsSpan[..i]));
													mountPointsSpan = mountPointsSpan[(i + 1)..];
												}
												else
													break;
											}
										}
										finally
										{
											ArrayPool<char>.Shared.Return(mountPoints);
										}
									}
									else
										lastError.VerifyWinapiNonzero();

									var volumeFlags = 0U;
									fixed (char* pLabel = label, pFsName = fsName)
										GetVolumeInformationW(pVolumeName, pLabel, labelLength, null, null, &volumeFlags, pFsName, fsNameLength);
									lastError = GetLastError();
									var fsNameSpan = lastError == ERROR_SUCCESS ? fsName.AsSpan().SliceFirst('\0') : [];
									var labelSpan = lastError == ERROR_SUCCESS ? label.AsSpan().SliceFirst('\0') : [];

									if (lastError != ERROR_SUCCESS && lastError != ERROR_UNRECOGNIZED_VOLUME)
										lastError.ThrowPlatformException();

									volumes.Add(new()
									{
										Name = new(volumeNameSpan),
										DeviceName = new(deviceNameSpan),
										MountPoints = [.. mp],
										FileSystemName = fsNameSpan.Length > 0 ? new(fsNameSpan) : null,
										Label = labelSpan.Length > 0 ? new(labelSpan) : null,
										IsReadOnly = (volumeFlags & FILE_READ_ONLY_VOLUME) != 0U
									});
								}

								// go to next volume
								fixed (char* pVolumeName = volumeName)
									lastError = FindNextVolumeW(hVolumeSearch, pVolumeName, volumeNameLength) ? ERROR_SUCCESS : GetLastError();
								if (lastError == ERROR_NO_MORE_FILES)
									break;
								else
									lastError.VerifyWinapiErrorCode();
							}
						}
						finally
						{
							FindVolumeClose(hVolumeSearch);
						}
					}
					finally
					{
						ArrayPool<char>.Shared.Return(fsName);
					}
				}
				finally
				{
					ArrayPool<char>.Shared.Return(label);
				}
			}
			finally
			{
				ArrayPool<char>.Shared.Return(deviceName);
			}
		}
		finally
		{
			ArrayPool<char>.Shared.Return(volumeName);
		}


		foreach (var volume in volumes)
		{
			var hFile = CreateFile(volume.Name.TrimEnd('\\'), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, null, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nint.Zero);
			if (hFile != INVALID_HANDLE_VALUE)
				try
				{
					var gli = new GET_LENGTH_INFORMATION();
					DeviceIoControl(hFile, IOCTL_DISK_GET_LENGTH_INFO, null, 0U, &gli, UMM.USizeOf(ref gli), null, null).VerifyWinapiNonzero();
					volume.Size = gli.Length;

					var sdn = new STORAGE_DEVICE_NUMBER();
					DeviceIoControl(hFile, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0U, &sdn, UMM.USizeOf(ref sdn), null, null).VerifyWinapiNonzero();
					volume.DeviceType = sdn.DeviceType;
					volume.PhysicalDeviceNumber = sdn.DeviceNumber;
				}
				finally
				{
					CloseHandle(hFile);
				}
			else
				throw GetLastError().ThrowPlatformException();
		}

		nint hPds;
		var perfObjects = new List<string>();
		PdhBindInputDataSourceW(&hPds, null).VerifyWinapiErrorCode();
		try
		{
		/*	var objectListLength = 0U;
			PdhEnumObjectsHW(hPds, null, null, &objectListLength, PERF_DETAIL_EXPERT, true).VerifyWinapiErrorCodeInList(ERROR_SUCCESS, PDH_MORE_DATA);
			var data = ArrayPool<char>.Shared.Rent((int)objectListLength);
			try
			{
				fixed (char* pData = data) 
					PdhEnumObjectsHW(hPds, null, pData, &objectListLength, PERF_DETAIL_EXPERT, false).VerifyWinapiErrorCode();

				var objects = data.AsSpan();
				while (true)
				{
					var i = objects.IndexOf('\0');
					if (i > 0)
					{
						perfObjects.Add(new(objects[..i]));
						objects = objects[(i + 1)..];
					}
					else
						break;
				}
			}
			finally
			{
				ArrayPool<char>.Shared.Return(data);
			}
		*/

			var counterListLength = 0U;
			var instanceListLength = 0U;
			fixed (char* pObjectName = "LogicalDisk")
			{
				PdhEnumObjectItemsHW(hPds, null, pObjectName, null, &counterListLength, null, &instanceListLength, PERF_DETAIL_EXPERT, 0U).VerifyWinapiErrorCodeInList(ERROR_SUCCESS, PDH_MORE_DATA);
				var counters = ArrayPool<char>.Shared.Rent((int)counterListLength);
				try
				{
					var instances = ArrayPool<char>.Shared.Rent((int)instanceListLength);
					try
					{
						fixed (char* pCounters = counters, pInstances = instances)
							PdhEnumObjectItemsHW(hPds, null, pObjectName, pCounters, &counterListLength, pInstances, &instanceListLength, PERF_DETAIL_EXPERT, 0U).VerifyWinapiErrorCode();

						var ctrs = counters.AsSpan();
						Console.WriteLine("Counters:");
						while (true)
						{
							var i = ctrs.IndexOf('\0');
							if (i > 0)
							{
								Console.WriteLine(new string(ctrs[..i]));
								ctrs = ctrs[(i + 1)..];
							}
							else
								break;
						}

						var inst = instances.AsSpan();
						Console.WriteLine("Instances:");
						while (true)
						{
							var i = inst.IndexOf('\0');
							if (i > 0)
							{
								perfObjects.Add(new string(inst[..i]));
								inst = inst[(i + 1)..];
							}
							else
								break;
						}
					}
					finally
					{
						ArrayPool<char>.Shared.Return(instances);
					}
				}
				finally
				{
					ArrayPool<char>.Shared.Return(counters);
				}
			}
		}
		finally
		{
			PdhCloseLog(hPds, 1);
		}

		perfObjects.Sort();
		foreach (var obj in perfObjects)
		{
			var volume = volumes.FirstOrDefault(v => v.MountPoints.Any(mp => string.Equals(mp, obj + @"\", StringComparison.Ordinal)));
			if (volume is null)
			{
				var deviceName = @"\Device\" + obj;
				volume = volumes.FirstOrDefault(v => string.Equals(v.DeviceName, deviceName, StringComparison.Ordinal));
			}
			if (volume is not null)
				volume.PerfCounterInstance = obj;

			Console.WriteLine(obj);
		}


		Console.WriteLine("Volumes:");
		foreach (var volume in volumes)
			Console.WriteLine(volume);
		Console.ReadLine();
	}
}
