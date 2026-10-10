/** Hands text to the browser as a file to save, without a round trip to the server. */
export function downloadText(text: string, fileName: string, type = "text/plain;charset=utf-8"): void {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  link.click();
  // The click starts the download synchronously; the URL is not needed after it.
  URL.revokeObjectURL(url);
}
