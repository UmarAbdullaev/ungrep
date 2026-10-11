# Building UI with ug

uGUI screen = about a dozen calls.
- Widgets from menu items (add Canvas + EventSystem too): `ug run "GameObject/UI (Canvas)/Button - TextMeshPro"` (also `Text - TextMeshPro`, `Image`, `Panel`). First TMP use: `ug run "Window/TextMeshPro/Import TMP Essential Resources"`.
- Layout and style in one `ug batch`: `set Canvas/Button anchorMin 0.5,0.5`, `anchorMax`, `pivot`, `sizeDelta 300,76`, `anchoredPosition 0,-40`, `color #2EB89E --component Image`, `fontSize 32`, `horizontalAlignment Center`. Siblings draw in order: `ug object move Canvas/Backdrop --index 0` sends a background behind.
- Click: `ug event add Canvas/Button onClick Canvas:UiHandler OnPlay`; check `ug event list Canvas/Button`.
- Look: `ug shot game`, then open the PNG. Overlay canvases are drawn. UI Toolkit (UXML/USS + UIDocument) is not (answer says `not drawn`): check in play mode or read the files.
- Style: one dark background, one accent colour with dark text on it, title at least 2x button font size, 300x76 buttons centred with gaps, round sizes. UI Toolkit: USS (`border-radius`, `:hover`, `:active`), not inline styles.
