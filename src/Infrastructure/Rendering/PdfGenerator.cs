using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Drawing;

namespace ZPL2PDF {
    /// <summary>
    /// Responsible for generating a PDF, adding each image (byte[] data) to a page.
    /// </summary>
    public static class PdfGenerator {
        private const double MmPerInch = 25.4;

        /// <summary>
        /// Adds one image to its own page, sized to the label's physical dimensions.
        /// Page size is in points (1/72"), so pixel counts must be converted via the render density (dpmm),
        /// otherwise the page is scaled by dpi/72 (≈2.8x at 203 dpi, ≈4.2x at 300 dpi).
        /// </summary>
        private static void AddImagePage(PdfDocument document, byte[] imageData, int dpi) {
            using var image = XImage.FromStream(new MemoryStream(imageData));

            // Images are rendered at dpmm = round(dpi / 25.4) dots per millimeter (matches LabelRenderer and Labelary).
            int dpmm = (int)Math.Round(dpi / MmPerInch);
            if (dpmm <= 0) dpmm = 8; // ~203 dpi fallback

            double widthMm = (double)image.PixelWidth / dpmm;
            double heightMm = (double)image.PixelHeight / dpmm;

            var page = document.AddPage();
            page.Width = XUnit.FromMillimeter(widthMm);
            page.Height = XUnit.FromMillimeter(heightMm);

            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }

        /// <summary>
        /// Generates a PDF with one image per page and saves the file to the specified path.
        /// </summary>
        /// <param name="imageDataList">List of image data in byte arrays.</param>
        /// <param name="outputPdf">Path to save the generated PDF file.</param>
        /// <param name="dpi">Render density used to produce the images (used to size pages to physical dimensions).</param>
        public static void GeneratePdf(List<byte[]> imageDataList, string outputPdf, int dpi) {
            using var document = new PdfDocument();
            foreach (var imageData in imageDataList) {
                AddImagePage(document, imageData, dpi);
            }
            document.Save(outputPdf);
        }

        /// <summary>
        /// Generates a PDF with one image per page and returns it as a byte array.
        /// </summary>
        /// <param name="imageDataList">List of image data in byte arrays.</param>
        /// <param name="dpi">Render density used to produce the images (used to size pages to physical dimensions).</param>
        /// <returns>PDF file as byte array.</returns>
        public static byte[] GeneratePdfToBytes(List<byte[]> imageDataList, int dpi) {
            using var document = new PdfDocument();
            foreach (var imageData in imageDataList) {
                AddImagePage(document, imageData, dpi);
            }

            using var stream = new MemoryStream();
            document.Save(stream);
            return stream.ToArray();
        }

        /// <summary>
        /// Merges multiple PDF documents (given as bytes) into a single PDF.
        /// </summary>
        public static byte[] MergePdfsToBytes(List<byte[]> pdfDocuments)
        {
            using var outputDocument = new PdfDocument();

            foreach (var pdfBytes in pdfDocuments)
            {
                if (pdfBytes == null || pdfBytes.Length == 0)
                    continue;

                using var ms = new MemoryStream(pdfBytes);
                using var inputDocument = PdfReader.Open(ms, PdfDocumentOpenMode.Import);

                for (int i = 0; i < inputDocument.PageCount; i++)
                {
                    outputDocument.AddPage(inputDocument.Pages[i]);
                }
            }

            using var outStream = new MemoryStream();
            outputDocument.Save(outStream, false);
            return outStream.ToArray();
        }
    }
}