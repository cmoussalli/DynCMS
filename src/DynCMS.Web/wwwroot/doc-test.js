document.addEventListener('DOMContentLoaded', function () {
  var el = document.getElementById('js-status');
  if (el) el.textContent = 'JS loaded OK';
});
document.documentElement.setAttribute('data-doc-js', 'ran');
window.docTestRan = true;
