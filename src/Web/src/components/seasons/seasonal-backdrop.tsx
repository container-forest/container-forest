import { useEffect, useRef, useSyncExternalStore } from "react";
import {
  type Particle,
  type Season,
  type SeasonStyle,
  particleCount,
  seasonPreferenceSnapshot,
  seasonStyles,
  shownSeason,
  spawnParticle,
  stepParticle,
  subscribeSeasonPreference,
  swayOffset,
} from "@/features/seasons/model";

/** The viewer's choice of season animation, shared by the backdrop and the setting that changes it. */
export function useSeasonPreference() {
  return useSyncExternalStore(subscribeSeasonPreference, seasonPreferenceSnapshot);
}

function useReducedMotion() {
  return useSyncExternalStore(
    (onChange) => {
      const query = matchMedia("(prefers-reduced-motion: reduce)");
      query.addEventListener("change", onChange);
      return () => query.removeEventListener("change", onChange);
    },
    () => matchMedia("(prefers-reduced-motion: reduce)").matches,
  );
}

/**
 * Leaves, butterflies or snowflakes drifting behind the panel, by season. Purely decorative: the canvas lies under
 * the page content, takes no clicks, is hidden from assistive technology, pauses in a hidden tab and is not drawn
 * at all when the system asks for reduced motion or the viewer turned it off.
 */
export function SeasonalBackdrop() {
  const preference = useSeasonPreference();
  const reducedMotion = useReducedMotion();
  const season = reducedMotion ? null : shownSeason(preference, new Date());

  return season ? <SeasonCanvas season={season} /> : null;
}

function SeasonCanvas({ season }: { season: Season }) {
  const canvasRef = useRef<HTMLCanvasElement>(null);

  // The animation loop draws on the canvas outside React: nothing here is data loading.
  useEffect(() => {
    const canvas = canvasRef.current;
    const context = canvas?.getContext("2d");
    if (!canvas || !context) return;

    const style = seasonStyles[season];
    let width = 0;
    let height = 0;
    let particles: Particle[] = [];

    const resize = () => {
      const ratio = Math.min(window.devicePixelRatio || 1, 2);
      width = window.innerWidth;
      height = window.innerHeight;
      canvas.width = Math.round(width * ratio);
      canvas.height = Math.round(height * ratio);
      context.setTransform(ratio, 0, 0, ratio, 0, 0);
      const count = particleCount(style, width, height);
      particles = particles.slice(0, count);
      while (particles.length < count) particles.push(spawnParticle(style, width, height, Math.random, true));
    };

    let frame = 0;
    let last = 0;
    const tick = (time: number) => {
      // A long pause (hidden tab, sleeping laptop) must not make particles jump.
      const dt = last === 0 ? 0 : Math.min((time - last) / 1000, 0.05);
      last = time;
      context.clearRect(0, 0, width, height);
      particles = particles.map((p) =>
        stepParticle(p, style, dt, width, height, Math.random) ? p : spawnParticle(style, width, height, Math.random, false),
      );
      for (const particle of particles) drawParticle(context, particle, style);
      frame = requestAnimationFrame(tick);
    };

    const onVisibility = () => {
      cancelAnimationFrame(frame);
      if (document.visibilityState === "visible") {
        last = 0;
        frame = requestAnimationFrame(tick);
      }
    };

    resize();
    window.addEventListener("resize", resize);
    document.addEventListener("visibilitychange", onVisibility);
    frame = requestAnimationFrame(tick);
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener("resize", resize);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [season]);

  return <canvas ref={canvasRef} aria-hidden className="pointer-events-none fixed inset-0 -z-10 size-full select-none" />;
}

function drawParticle(context: CanvasRenderingContext2D, particle: Particle, style: SeasonStyle) {
  context.save();
  context.globalAlpha = particle.opacity;
  context.translate(particle.x + (style.shape === "butterfly" ? 0 : swayOffset(particle)), particle.y);
  context.fillStyle = particle.color;
  context.strokeStyle = particle.color;

  switch (style.shape) {
    case "leaf":
      drawLeaf(context, particle);
      break;
    case "butterfly":
      drawButterfly(context, particle);
      break;
    case "snowflake":
      drawSnowflake(context, particle);
      break;
  }
  context.restore();
}

function drawLeaf(context: CanvasRenderingContext2D, { size, rotation, phase }: Particle) {
  context.rotate(rotation);
  // Tumbling: the leaf turns its edge to the viewer now and then.
  context.scale(0.7 + 0.3 * Math.cos(phase * 1.3), 1);
  context.beginPath();
  context.moveTo(0, -size);
  context.quadraticCurveTo(size * 1.15, -size * 0.25, 0, size);
  context.quadraticCurveTo(-size * 1.15, -size * 0.25, 0, -size);
  context.fill();

  context.globalAlpha *= 0.5;
  context.strokeStyle = "rgba(20, 40, 30, 0.6)";
  context.lineWidth = 0.8;
  context.beginPath();
  context.moveTo(0, -size * 0.75);
  context.lineTo(0, size * 1.25);
  context.stroke();
}

function drawButterfly(context: CanvasRenderingContext2D, { size, phase, heading }: Particle) {
  // A slight tilt towards where it flies, and wings that open and close.
  context.rotate(Math.cos(heading) * 0.35);
  const open = 0.2 + 0.8 * Math.abs(Math.sin(phase * 6));
  for (const side of [-1, 1]) {
    context.beginPath();
    context.ellipse(side * size * 0.55 * open, -size * 0.25, size * 0.6 * open, size * 0.5, side * 0.4, 0, Math.PI * 2);
    context.ellipse(side * size * 0.4 * open, size * 0.35, size * 0.42 * open, size * 0.34, -side * 0.3, 0, Math.PI * 2);
    context.fill();
  }
  context.fillStyle = "rgba(40, 45, 35, 0.8)";
  context.beginPath();
  context.ellipse(0, 0, size * 0.09, size * 0.6, 0, 0, Math.PI * 2);
  context.fill();
}

function drawSnowflake(context: CanvasRenderingContext2D, { size, phase }: Particle) {
  if (size < 3.5) {
    context.beginPath();
    context.arc(0, 0, size / 2, 0, Math.PI * 2);
    context.fill();
    return;
  }

  context.rotate(phase * 0.3);
  context.lineWidth = 1;
  context.lineCap = "round";
  context.beginPath();
  for (let arm = 0; arm < 3; arm++) {
    const angle = (arm * Math.PI) / 3;
    context.moveTo(Math.cos(angle) * size, Math.sin(angle) * size);
    context.lineTo(-Math.cos(angle) * size, -Math.sin(angle) * size);
  }
  context.stroke();
}
