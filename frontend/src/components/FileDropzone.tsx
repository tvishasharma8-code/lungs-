import React, { useCallback, useRef, useState } from "react";

interface Props {
  onFilesSelected: (files: File[]) => void;
  selectedFolderName: string | null;
  selectedFileCount: number;
  disabled?: boolean;
}

// Recursively reads a dropped folder's contents via the WebKit DataTransferItem API.
async function readEntry(entry: any, out: File[]): Promise<void> {
  if (entry.isFile) {
    const file: File = await new Promise((resolve, reject) => entry.file(resolve, reject));
    out.push(file);
  } else if (entry.isDirectory) {
    const reader = entry.createReader();
    const entries: any[] = await new Promise((resolve, reject) =>
      reader.readEntries(resolve, reject)
    );
    for (const child of entries) {
      await readEntry(child, out);
    }
  }
}

export default function FileDropzone({
  onFilesSelected,
  selectedFolderName,
  selectedFileCount,
  disabled,
}: Props) {
  const [isDragOver, setIsDragOver] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);

  const handleDrop = useCallback(
    async (e: React.DragEvent<HTMLDivElement>) => {
      e.preventDefault();
      setIsDragOver(false);
      if (disabled) return;

      const items = e.dataTransfer.items;
      const files: File[] = [];
      if (items && items.length > 0 && (items[0] as any).webkitGetAsEntry) {
        for (const item of Array.from(items)) {
          const entry = (item as any).webkitGetAsEntry?.();
          if (entry) await readEntry(entry, files);
        }
      } else {
        files.push(...Array.from(e.dataTransfer.files));
      }
      if (files.length > 0) onFilesSelected(files);
    },
    [onFilesSelected, disabled]
  );

  const handleBrowse = (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files ? Array.from(e.target.files) : [];
    if (files.length > 0) onFilesSelected(files);
  };

  return (
    <div
      onDragOver={(e) => {
        e.preventDefault();
        if (!disabled) setIsDragOver(true);
      }}
      onDragLeave={() => setIsDragOver(false)}
      onDrop={handleDrop}
      className={[
        "flex flex-col items-center justify-center gap-3 rounded-xl border-2 border-dashed px-6 py-14 text-center transition-colors",
        isDragOver ? "border-accent-400 bg-accent-500/5" : "border-white/10 bg-white/[0.015]",
        disabled ? "opacity-50" : "",
      ].join(" ")}
    >
      <span className="flex h-14 w-14 items-center justify-center rounded-xl bg-accent-500/10 text-accent-300">
        <FolderIcon />
      </span>
      <div>
        <p className="font-display text-sm font-medium text-slate-200">Drag &amp; Drop DICOM Folder Here</p>
        <p className="mt-1 text-xs text-slate-500">or</p>
      </div>
      <button
        type="button"
        disabled={disabled}
        className="btn-secondary"
        onClick={() => inputRef.current?.click()}
      >
        Browse Folder
      </button>
      <input
        ref={inputRef}
        type="file"
        // @ts-expect-error non-standard attributes for folder selection
        webkitdirectory="true"
        directory=""
        multiple
        hidden
        onChange={handleBrowse}
      />

      {selectedFolderName && (
        <div className="mt-4 flex w-full max-w-sm items-center gap-3 rounded-lg border border-white/10 bg-white/[0.03] px-4 py-3 text-left">
          <span className="flex h-8 w-8 items-center justify-center rounded-md bg-accent-500/15 text-accent-300">
            <FolderIcon small />
          </span>
          <div className="flex-1 overflow-hidden">
            <p className="truncate text-sm text-slate-200">{selectedFolderName}</p>
            <p className="text-xs text-slate-500">{selectedFileCount} files detected</p>
          </div>
          <span className="text-glow-cyan">
            <CheckCircle />
          </span>
        </div>
      )}
    </div>
  );
}

function FolderIcon({ small }: { small?: boolean }) {
  const size = small ? 16 : 26;
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none">
      <path
        d="M3 7a2 2 0 012-2h4l2 2h8a2 2 0 012 2v8a2 2 0 01-2 2H5a2 2 0 01-2-2V7z"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinejoin="round"
      />
    </svg>
  );
}
function CheckCircle() {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none">
      <circle cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="1.6" />
      <path d="M8 12.5l2.5 2.5L16 9.5" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
