using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace ironmanx_04_2.Ai
{
	/// <summary>
	/// Converts PDF pages to PNG images using the built-in Windows PDF renderer (Windows.Data.Pdf, Windows 10+).
	/// No external dependency; WinRT async operations are waited on directly.
	/// </summary>
	public static class PdfPageImager
	{
		private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

		public static List<byte[]> RenderPages(byte[] pdf, int maxPages, uint widthPixels = 1400)
		{
			List<byte[]> result = null;
			Exception error = null;
			// Run on an MTA thread so WinRT async completion never depends on the calling STA's message pump.
			var t = new Thread(() =>
			{
				try { result = RenderCore(pdf, maxPages, widthPixels); }
				catch (Exception ex) { error = ex; }
			});
			t.IsBackground = true;
			t.SetApartmentState(ApartmentState.MTA);
			t.Start();
			if (!t.Join(TimeSpan.FromSeconds(180))) throw new TimeoutException("PDF page rendering timed out.");
			if (error != null) throw error;
			return result;
		}

		private static List<byte[]> RenderCore(byte[] pdf, int maxPages, uint widthPixels)
		{
			var images = new List<byte[]>();
			using (var input = new InMemoryRandomAccessStream())
			{
				var writer = new DataWriter(input.GetOutputStreamAt(0));
				writer.WriteBytes(pdf);
				Wait(writer.StoreAsync());
				Wait(writer.FlushAsync());
				writer.DetachStream();
				writer.Dispose();
				input.Seek(0);

				var doc = Wait(PdfDocument.LoadFromStreamAsync(input));
				var count = (int)Math.Min(doc.PageCount, (uint)Math.Max(1, maxPages));
				for (uint i = 0; i < count; i++)
				{
					using (var page = doc.GetPage(i))
					using (var output = new InMemoryRandomAccessStream())
					{
						var options = new PdfPageRenderOptions { DestinationWidth = widthPixels };
						Wait(page.RenderToStreamAsync(output, options));
						var size = (uint)output.Size;
						var reader = new DataReader(output.GetInputStreamAt(0));
						Wait(reader.LoadAsync(size));
						var bytes = new byte[size];
						reader.ReadBytes(bytes);
						reader.DetachStream();
						reader.Dispose();
						images.Add(bytes);
					}
				}
			}
			return images;
		}

		private static void WaitInfo(IAsyncInfo info)
		{
			var deadline = DateTime.UtcNow + Timeout;
			while (info.Status == AsyncStatus.Started)
			{
				if (DateTime.UtcNow > deadline) { info.Cancel(); throw new TimeoutException("Windows PDF renderer timed out."); }
				Thread.Sleep(5);
			}
			if (info.Status == AsyncStatus.Error) throw info.ErrorCode ?? new InvalidOperationException("Windows PDF renderer failed.");
			if (info.Status == AsyncStatus.Canceled) throw new OperationCanceledException();
		}

		private static void Wait(IAsyncAction op)
		{
			WaitInfo(op);
			op.GetResults();
		}

		private static T Wait<T>(IAsyncOperation<T> op)
		{
			WaitInfo(op);
			return op.GetResults();
		}

		private static T Wait<T, TProgress>(IAsyncOperationWithProgress<T, TProgress> op)
		{
			WaitInfo(op);
			return op.GetResults();
		}
	}
}
