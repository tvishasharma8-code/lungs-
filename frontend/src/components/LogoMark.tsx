interface Props {
  size?: number;
  className?: string;
}

/**
 * The LungVision mark: the original lung/trachea icon's left side
 * (trachea stem + left lung bulb) kept exactly as-is, with the right
 * side replaced by a clean "V" instead of the mirrored lung bulb.
 */
export default function LogoMark({ size = 18, className }: Props) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      className={className}
    >
      <path
        d="M12 2v8m0 0c-1.5-3-4-4-6-3.5C3 7.5 2.5 12 3 15c.4 3 2 6 4 6 1.4 0 2-1 2-3v-8M15 2L19 21L23 2"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}
