// A list row that opens its item as a whole: the item's link stretches over the row (::after), so the row works
// with the mouse, a finger, the keyboard and a screen reader like one link, and Cmd/Ctrl-click opens a new tab.
// Secondary actions in the row are lifted above the stretched link and stay clickable on their own.

/** For the row (`<tr>`, or a card): the anchor of the stretched link, with the hover of a clickable thing. */
export const rowClass = "relative cursor-pointer hover:bg-muted/50 focus-within:bg-muted/50";

/** For the item's main link, usually its name. */
export const rowLinkClass =
  "outline-none after:absolute after:inset-0 after:content-[''] focus-visible:after:rounded-md focus-visible:after:ring-2 focus-visible:after:ring-ring";

/** For a cell or wrapper with buttons and links of their own, so they are not covered by the row link. */
export const rowActionsClass = "relative z-10 cursor-auto";
