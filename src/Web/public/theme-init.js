// Sets the theme before the first paint, so a light device does not see the dark panel flash (and the reverse).
// Same rules as src/features/theme/model.ts: the choice stored by the toggle, otherwise the device's theme; and the
// colour theme chosen on the account page, otherwise the forest one.
(function () {
  var theme;
  try {
    theme = localStorage.getItem("cf-theme");
  } catch (e) {}
  if (theme !== "light" && theme !== "dark") theme = matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  document.documentElement.classList.toggle("dark", theme === "dark");
  document.documentElement.style.colorScheme = theme;
  var palette;
  try {
    palette = localStorage.getItem("cf-palette");
  } catch (e) {}
  if (palette === "meadow" || palette === "lilac" || palette === "lake" || palette === "sunset") document.documentElement.dataset.palette = palette;
})();
