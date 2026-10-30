/* Served by DynCMS at /_content/DynCMS.Plugin.Forms/forms.js while the plugin runs.
   One delegated listener on the document: forms that appear later (Blazor navigation, enhanced loads) work too,
   and without this script the form is an ordinary POST. */
(function () {
    if (window.__dcForms) return;
    window.__dcForms = true;

    function setStatus(form, text, kind) {
        var status = form.querySelector('.dcf-status');
        if (!status) return;
        status.textContent = text || '';
        status.hidden = !text;
        status.className = 'dcf-status' + (kind ? ' dcf-' + kind : '');
    }

    function clearErrors(form) {
        form.querySelectorAll('.dcf-error').forEach(function (e) { e.hidden = true; e.textContent = ''; });
        form.querySelectorAll('.dcf-invalid').forEach(function (e) { e.classList.remove('dcf-invalid'); });
    }

    function showErrors(form, errors) {
        Object.keys(errors || {}).forEach(function (name) {
            var field = form.querySelector('[data-field="' + name.replace(/"/g, '') + '"]');
            if (!field) return;
            field.classList.add('dcf-invalid');
            var box = field.querySelector('.dcf-error');
            if (box) { box.textContent = errors[name]; box.hidden = false; }
        });
    }

    document.addEventListener('submit', function (event) {
        var form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-dcforms')) return;
        event.preventDefault();

        var button = form.querySelector('.dcf-submit');
        clearErrors(form);
        setStatus(form, '', '');
        if (button) button.disabled = true;

        fetch(form.action, {
            method: 'POST',
            headers: { 'Accept': 'application/json', 'Content-Type': 'application/x-www-form-urlencoded' },
            body: new URLSearchParams(new FormData(form)).toString()
        }).then(function (response) {
            return response.json().catch(function () { return {}; }).then(function (body) { return { ok: response.ok, body: body }; });
        }).then(function (r) {
            if (r.ok && r.body.ok) {
                form.reset();
                var redirect = r.body.redirect || form.getAttribute('data-redirect');
                if (redirect) { window.location.href = redirect; return; }
                setStatus(form, form.getAttribute('data-success') || 'Thank you!', 'ok');
            } else {
                showErrors(form, r.body.errors);
                setStatus(form, r.body.message || 'The form could not be sent.', 'error');
            }
        }).catch(function () {
            setStatus(form, 'The form could not be sent. Please try again.', 'error');
        }).then(function () {
            if (button) button.disabled = false;
        });
    });
})();
