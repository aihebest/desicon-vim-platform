// Prevents a double-click from sending the same invoice twice (the server also guards).
document.addEventListener("submit", function (e) {
  var form = e.target;
  if (form.dataset.sent === "1") { e.preventDefault(); return; }
  form.dataset.sent = "1";
  form.querySelectorAll("button[type=submit]").forEach(function (b) {
    b.disabled = true;
    b.textContent = "Uploading, please wait…";
  });
});
