window.cookieConsent = {
    hasConsent: function () {
        return document.cookie.split(';').some(c => c.trim().startsWith('cookie_consent='));
    },
    accept: function () {
        var d = new Date();
        d.setFullYear(d.getFullYear() + 1);
        document.cookie = 'cookie_consent=accepted;path=/;expires=' + d.toUTCString() + ';SameSite=Strict';
    }
};
