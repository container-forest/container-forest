// Sets the theme before the first paint, so a light device does not see the dark panel flash (and the reverse).
// Same rule as src/features/theme/model.ts: the choice stored by the toggle, otherwise the device's theme.
(function () {
  var theme;
  try {
    theme = localStorage.getItem("cf-theme");
  } catch (e) {}
  if (theme !== "light" && theme !== "dark") theme = matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  document.documentElement.classList.toggle("dark", theme === "dark");
  document.documentElement.style.colorScheme = theme;
})();
