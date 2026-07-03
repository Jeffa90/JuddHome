/* JuddHome — replaces the Jellyfin home screen with Netflix-style rows.
   Loaded on every page via a script tag injected into index.html; it only
   activates on the home route. Uses the existing window.ApiClient for auth —
   no separate auth system. */
(function () {
    'use strict';

    var HERO_MAX_WIDTH = 1920;
    var CARD_MAX_WIDTH = 400;
    var STATE = {
        template: null,
        heroTimer: null,
        heroIndex: 0,
        heroItems: [],
        heroSeconds: 8,
        sections: [],
        layout: null
    };

    /* ---------- helpers ---------- */

    // Jellyfin's API serialises with PascalCase; be tolerant of camelCase too.
    function prop(obj, name) {
        if (!obj) { return undefined; }
        if (obj[name] !== undefined) { return obj[name]; }
        var camel = name.charAt(0).toLowerCase() + name.slice(1);
        return obj[camel];
    }

    function apiClient() {
        return window.ApiClient || null;
    }

    function loggedIn() {
        var api = apiClient();
        try {
            return !!(api && api.accessToken() && api.getCurrentUserId());
        } catch (e) {
            return false;
        }
    }

    function apiUrl(path, params) {
        var api = apiClient();
        var url = api.getUrl(path);
        if (params) {
            var q = Object.keys(params).map(function (k) {
                return encodeURIComponent(k) + '=' + encodeURIComponent(params[k]);
            }).join('&');
            url += (url.indexOf('?') === -1 ? '?' : '&') + q;
        }
        return url;
    }

    function getJson(path) {
        return apiClient().getJSON(apiUrl(path));
    }

    function postJson(path, body) {
        return apiClient().ajax({
            type: 'POST',
            url: apiUrl(path),
            data: JSON.stringify(body || {}),
            contentType: 'application/json'
        });
    }

    function imageUrl(itemId, imageType, maxWidth) {
        // Image endpoints need the auth token as a query param because <img>
        // tags cannot send Authorization headers.
        return apiUrl('Items/' + itemId + '/Images/' + imageType, {
            maxWidth: maxWidth,
            api_key: apiClient().accessToken()
        });
    }

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) { node.className = className; }
        if (text !== undefined && text !== null) { node.textContent = text; }
        return node;
    }

    function navigateToItem(itemId) {
        var api = apiClient();
        var serverId = api.serverId ? api.serverId() : null;
        try {
            if (window.Emby && window.Emby.Page && typeof window.Emby.Page.showItem === 'function') {
                window.Emby.Page.showItem(itemId, serverId);
                return;
            }
        } catch (e) { /* fall through to hash navigation */ }
        var hash = '#/details?id=' + itemId + (serverId ? '&serverId=' + serverId : '');
        if (window.Dashboard && typeof window.Dashboard.navigate === 'function') {
            window.Dashboard.navigate(hash);
        } else {
            window.location.hash = hash;
        }
    }

    // Playback: resolve the real playable item server-side (series -> next
    // episode), navigate to its detail page, then press Jellyfin's own play
    // button so all playback goes through Jellyfin's native playback manager.
    function playItem(itemId) {
        getJson('JuddHome/PlayTarget/' + itemId).then(function (res) {
            var playId = prop(res, 'PlayItemId') || itemId;
            navigateToItem(playId);
            var attempts = 0;
            var poll = setInterval(function () {
                attempts++;
                var btn = document.querySelector(
                    '.mainDetailButtons .btnPlaystate, .mainDetailButtons [data-action="resume"], ' +
                    '.detailPagePrimaryContainer .btnPlay, .mainDetailButtons .btnPlay');
                if (btn) {
                    clearInterval(poll);
                    btn.click();
                } else if (attempts > 12) {
                    clearInterval(poll);
                }
            }, 250);
        }).catch(function (err) {
            console.warn('JuddHome: play failed', err);
            navigateToItem(itemId);
        });
    }

    /* ---------- route detection ---------- */

    function isHomeRoute() {
        var hash = window.location.hash || '';
        var path = window.location.pathname || '';
        return hash.indexOf('home.html') !== -1
            || hash === '#/' || hash === '#/home' || hash === ''
            || /\/home(\.html)?$/.test(path);
    }

    function findHomeSectionsContainer() {
        var page = document.querySelector('.homePage:not(.hide)') || document.querySelector('.homePage');
        if (!page) { return null; }
        return page.querySelector('.homeSectionsContainer');
    }

    /* ---------- template ---------- */

    function loadTemplate() {
        if (STATE.template) { return Promise.resolve(STATE.template); }
        return fetch(apiClient().getUrl('JuddHome/Web/home.html'))
            .then(function (r) {
                if (!r.ok) { throw new Error('template ' + r.status); }
                return r.text();
            })
            .then(function (html) {
                STATE.template = html;
                return html;
            });
    }

    function ensureStyles() {
        if (document.getElementById('juddhome-css')) { return; }
        var link = document.createElement('link');
        link.id = 'juddhome-css';
        link.rel = 'stylesheet';
        link.href = apiClient().getUrl('JuddHome/Web/home.css');
        document.head.appendChild(link);
    }

    /* ---------- cards ---------- */

    function cardImage(item, container) {
        var backdropId = prop(item, 'BackdropItemId');
        var hasPrimary = prop(item, 'HasPrimary');
        var id = prop(item, 'Id');
        var name = prop(item, 'Name') || '';
        var src = null;
        if (backdropId) {
            src = imageUrl(backdropId, 'Backdrop', CARD_MAX_WIDTH);
        } else if (hasPrimary) {
            src = imageUrl(id, 'Primary', CARD_MAX_WIDTH);
        }

        if (!src) {
            container.appendChild(el('div', 'jh-card-placeholder', name));
            return;
        }

        var img = document.createElement('img');
        img.className = 'jh-card-img';
        img.loading = 'lazy';
        img.alt = name;
        img.onerror = function () {
            // Never show a broken image icon — swap in a styled placeholder.
            var ph = el('div', 'jh-card-placeholder', name);
            if (img.parentNode) { img.parentNode.replaceChild(ph, img); }
        };
        img.src = src;
        container.appendChild(img);
    }

    function buildCard(item) {
        var card = el('div', 'jh-card');
        var id = prop(item, 'Id');
        var name = prop(item, 'Name') || '';
        var type = prop(item, 'Type') || '';
        var seriesId = prop(item, 'SeriesId');
        var seriesName = prop(item, 'SeriesName');
        var progress = prop(item, 'ProgressPercent');
        var overview = prop(item, 'Overview') || '';

        card.tabIndex = 0;
        cardImage(item, card);

        var label = el('div', 'jh-card-label', seriesName || name);
        if (seriesName && name && seriesName !== name) {
            label.appendChild(el('span', 'jh-card-sub', name));
        }
        card.appendChild(label);

        if (typeof progress === 'number' && progress > 0 && progress < 100) {
            var bar = el('div', 'jh-card-progress');
            var fill = el('div', 'jh-card-progress-fill');
            fill.style.width = progress + '%';
            bar.appendChild(fill);
            card.appendChild(bar);
        }

        var hover = el('div', 'jh-card-hover');
        hover.appendChild(el('div', 'jh-card-hover-title', seriesName || name));
        if (overview) {
            hover.appendChild(el('p', 'jh-card-hover-overview', overview));
        }
        var playBtn = el('button', 'jh-card-play', '▶ Play');
        playBtn.type = 'button';
        playBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            // Episodes play themselves; series resolve to next-up server-side.
            playItem(type === 'Episode' ? id : (type === 'Series' ? id : id));
        });
        hover.appendChild(playBtn);
        card.appendChild(hover);

        // Movies open the movie detail page; TV episodes/shows open the show's
        // detail page so the user picks the episode — never auto-play on click.
        card.addEventListener('click', function () {
            if (type === 'Episode' && seriesId) {
                navigateToItem(seriesId);
            } else {
                navigateToItem(id);
            }
        });
        card.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { card.click(); }
        });
        return card;
    }

    function buildScroller(items) {
        var scroller = el('div', 'jh-scroller');
        items.forEach(function (item) {
            scroller.appendChild(buildCard(item));
        });
        // Desktop: mouse wheel scrolls the row horizontally.
        scroller.addEventListener('wheel', function (e) {
            if (Math.abs(e.deltaY) > Math.abs(e.deltaX)) {
                e.preventDefault();
                scroller.scrollLeft += e.deltaY;
            }
        }, { passive: false });
        return scroller;
    }

    function buildRow(title, items) {
        var row = el('section', 'jh-row');
        row.appendChild(el('h2', 'jh-row-title', title));
        row.appendChild(buildScroller(items));
        return row;
    }

    /* ---------- hero banner ---------- */

    function stopHero() {
        if (STATE.heroTimer) {
            clearInterval(STATE.heroTimer);
            STATE.heroTimer = null;
        }
    }

    function showHeroSlide(root, index) {
        var slides = root.querySelectorAll('.jh-hero-slide');
        var dots = root.querySelectorAll('.jh-hero-dot');
        if (!slides.length) { return; }
        STATE.heroIndex = (index + slides.length) % slides.length;
        slides.forEach(function (s, i) {
            s.classList.toggle('jh-active', i === STATE.heroIndex);
        });
        dots.forEach(function (d, i) {
            d.classList.toggle('jh-active', i === STATE.heroIndex);
        });
    }

    function startHeroTimer(root) {
        stopHero();
        if (STATE.heroItems.length < 2) { return; }
        STATE.heroTimer = setInterval(function () {
            showHeroSlide(root, STATE.heroIndex + 1);
        }, Math.max(3, STATE.heroSeconds) * 1000);
    }

    function renderHero(root, items) {
        var hero = root.querySelector('#jh-hero');
        if (!hero || !items.length) { return; }
        STATE.heroItems = items;
        var slidesBox = hero.querySelector('.jh-hero-slides');
        var dotsBox = hero.querySelector('.jh-hero-dots');
        slidesBox.textContent = '';
        dotsBox.textContent = '';

        items.forEach(function (item, i) {
            var slide = el('div', 'jh-hero-slide');
            var id = prop(item, 'Id');
            var backdropId = prop(item, 'BackdropItemId') || id;
            var name = prop(item, 'Name') || '';

            var img = document.createElement('img');
            img.className = 'jh-hero-backdrop';
            img.alt = name;
            img.onerror = function () {
                var ph = el('div', 'jh-card-placeholder', name);
                if (img.parentNode) { img.parentNode.replaceChild(ph, img); }
            };
            img.src = imageUrl(backdropId, 'Backdrop', HERO_MAX_WIDTH);
            slide.appendChild(img);
            slide.appendChild(el('div', 'jh-hero-shade'));

            var content = el('div', 'jh-hero-content');
            content.appendChild(el('h1', 'jh-hero-title', prop(item, 'SeriesName') || name));

            var genres = prop(item, 'Genres') || [];
            if (genres.length) {
                var tags = el('div', 'jh-hero-tags');
                genres.slice(0, 3).forEach(function (g) {
                    tags.appendChild(el('span', 'jh-hero-tag', g));
                });
                content.appendChild(tags);
            }

            var overview = prop(item, 'Overview');
            if (overview) {
                content.appendChild(el('p', 'jh-hero-overview', overview));
            }

            var buttons = el('div', 'jh-hero-buttons');
            var play = el('button', 'jh-btn jh-btn-play', '▶ Play');
            play.type = 'button';
            play.addEventListener('click', function (e) {
                e.stopPropagation();
                playItem(id);
            });
            var info = el('button', 'jh-btn jh-btn-info', 'ⓘ More Info');
            info.type = 'button';
            info.addEventListener('click', function (e) {
                e.stopPropagation();
                navigateToItem(prop(item, 'Type') === 'Episode' && prop(item, 'SeriesId')
                    ? prop(item, 'SeriesId')
                    : id);
            });
            buttons.appendChild(play);
            buttons.appendChild(info);
            content.appendChild(buttons);
            slide.appendChild(content);
            slidesBox.appendChild(slide);

            var dot = el('button', 'jh-hero-dot');
            dot.type = 'button';
            dot.setAttribute('aria-label', 'Slide ' + (i + 1));
            dot.addEventListener('click', function () {
                showHeroSlide(hero, i);
                startHeroTimer(hero);
            });
            dotsBox.appendChild(dot);
        });

        hero.querySelector('.jh-hero-prev').onclick = function () {
            showHeroSlide(hero, STATE.heroIndex - 1);
            startHeroTimer(hero);
        };
        hero.querySelector('.jh-hero-next').onclick = function () {
            showHeroSlide(hero, STATE.heroIndex + 1);
            startHeroTimer(hero);
        };
        hero.addEventListener('mouseenter', stopHero);
        hero.addEventListener('mouseleave', function () { startHeroTimer(hero); });

        hero.hidden = false;
        showHeroSlide(hero, 0);
        startHeroTimer(hero);
    }

    /* ---------- sections ---------- */

    function skeletonRow(itemCount) {
        var row = el('section', 'jh-row jh-skeleton');
        row.appendChild(el('div', 'jh-skeleton-title jh-pulse'));
        var scroller = el('div', 'jh-scroller');
        for (var i = 0; i < Math.min(itemCount, 8); i++) {
            scroller.appendChild(el('div', 'jh-skeleton-card jh-pulse'));
        }
        row.appendChild(scroller);
        return row;
    }

    function renderMyListPrompt(row, title) {
        row.textContent = '';
        row.appendChild(el('h2', 'jh-row-title', title));
        var prompt = el('div', 'jh-mylist-prompt');
        prompt.appendChild(el('span', null,
            'Create a playlist called "My List" to pin your favourites to the home screen.'));
        var btn = el('button', 'jh-btn jh-btn-accent', 'Create My List');
        btn.type = 'button';
        btn.addEventListener('click', function () {
            btn.disabled = true;
            postJson('JuddHome/MyList/Create').then(function () {
                prompt.textContent = '"My List" created! Add items to the playlist and they will appear here.';
            }).catch(function () {
                btn.disabled = false;
                prompt.appendChild(el('div', 'jh-row-error', 'Could not create the playlist.'));
            });
        });
        prompt.appendChild(btn);
        row.appendChild(prompt);
    }

    function loadSection(descriptor, container) {
        var type = prop(descriptor, 'SectionType');
        var placeholder = skeletonRow(6);
        container.appendChild(placeholder);

        return getJson('JuddHome/Section/' + type).then(function (section) {
            var items = prop(section, 'Items') || [];
            var rows = prop(section, 'Rows');
            var title = prop(section, 'Title') || prop(descriptor, 'Title');
            var emptyAction = prop(section, 'EmptyAction');

            if (emptyAction === 'CreateMyList') {
                renderMyListPrompt(placeholder, title);
                placeholder.classList.remove('jh-skeleton');
                return;
            }

            if (rows && rows.length) {
                var frag = document.createDocumentFragment();
                rows.forEach(function (r) {
                    var rowItems = prop(r, 'Items') || [];
                    if (rowItems.length) {
                        frag.appendChild(buildRow(prop(r, 'Title'), rowItems));
                    }
                });
                if (frag.childNodes.length) {
                    container.replaceChild(frag, placeholder);
                } else {
                    placeholder.remove(); // silently skip empty sections
                }
                return;
            }

            if (!items.length) {
                placeholder.remove(); // silently skip empty sections
                return;
            }

            container.replaceChild(buildRow(title, items), placeholder);
        }).catch(function (err) {
            // One failed section must never break the rest of the page.
            console.warn('JuddHome: section failed', type, err);
            placeholder.textContent = '';
            placeholder.classList.remove('jh-skeleton');
            placeholder.appendChild(el('h2', 'jh-row-title', prop(descriptor, 'Title') || type));
            placeholder.appendChild(el('div', 'jh-row-error', 'Could not load this section'));
        });
    }

    function loadHero(root) {
        return getJson('JuddHome/Hero').then(function (items) {
            renderHero(root, items || []);
        }).catch(function (err) {
            console.warn('JuddHome: hero failed', err);
            var hero = root.querySelector('#jh-hero');
            if (hero) { hero.hidden = true; }
        });
    }

    /* ---------- settings modal ---------- */

    function openSettings(root) {
        var modal = root.querySelector('#jh-settings-modal');
        var list = root.querySelector('#jh-settings-list');
        if (!modal || !list) { return; }
        list.textContent = '';

        getJson('JuddHome/Config/User').then(function (prefs) {
            var order = prop(prefs, 'SectionOrder') || [];
            var disabled = (prop(prefs, 'DisabledSections') || []).map(function (s) { return s.toLowerCase(); });
            var titles = {};
            (STATE.layout ? prop(STATE.layout, 'Sections') || [] : []).forEach(function (s) {
                titles[prop(s, 'SectionType')] = prop(s, 'Title');
            });

            order.forEach(function (sectionType) {
                var li = el('li', 'jh-settings-item');
                li.draggable = true;
                li.dataset.section = sectionType;

                li.appendChild(el('span', 'jh-drag-handle', '☰'));

                var label = el('label');
                var cb = document.createElement('input');
                cb.type = 'checkbox';
                cb.checked = disabled.indexOf(sectionType.toLowerCase()) === -1;
                label.appendChild(cb);
                label.appendChild(document.createTextNode(' ' + (titles[sectionType] || sectionType)));
                li.appendChild(label);

                var btns = el('span', 'jh-order-btns');
                var up = el('button', null, '↑');
                up.type = 'button';
                up.addEventListener('click', function () {
                    if (li.previousElementSibling) { list.insertBefore(li, li.previousElementSibling); }
                });
                var down = el('button', null, '↓');
                down.type = 'button';
                down.addEventListener('click', function () {
                    if (li.nextElementSibling) { list.insertBefore(li.nextElementSibling, li); }
                });
                btns.appendChild(up);
                btns.appendChild(down);
                li.appendChild(btns);

                li.addEventListener('dragstart', function (e) {
                    li.classList.add('jh-dragging');
                    e.dataTransfer.effectAllowed = 'move';
                });
                li.addEventListener('dragend', function () {
                    li.classList.remove('jh-dragging');
                });
                li.addEventListener('dragover', function (e) {
                    e.preventDefault();
                    var dragging = list.querySelector('.jh-dragging');
                    if (dragging && dragging !== li) {
                        var rect = li.getBoundingClientRect();
                        var before = (e.clientY - rect.top) < rect.height / 2;
                        list.insertBefore(dragging, before ? li : li.nextSibling);
                    }
                });

                list.appendChild(li);
            });

            modal.hidden = false;
        }).catch(function (err) {
            console.warn('JuddHome: could not load preferences', err);
        });

        modal.querySelector('.jh-modal-backdrop').onclick = closeSettings;
        modal.querySelector('#jh-settings-cancel').onclick = closeSettings;
        modal.querySelector('#jh-settings-save').onclick = function () {
            var items = Array.prototype.slice.call(list.querySelectorAll('.jh-settings-item'));
            var newOrder = items.map(function (li) { return li.dataset.section; });
            var newDisabled = items
                .filter(function (li) { return !li.querySelector('input').checked; })
                .map(function (li) { return li.dataset.section; });
            postJson('JuddHome/Config/User', {
                SectionOrder: newOrder,
                DisabledSections: newDisabled
            }).then(function () {
                closeSettings();
                var container = findHomeSectionsContainer();
                var mount = document.getElementById('juddhome-root');
                if (mount && mount.parentNode) {
                    mount.parentNode.removeChild(mount);
                }
                if (container) { takeOver(container); }
            }).catch(function (err) {
                console.warn('JuddHome: saving preferences failed', err);
            });
        };

        function closeSettings() {
            modal.hidden = true;
        }
    }

    // "Hamburger menu -> JuddHome Settings": add an entry to Jellyfin's drawer.
    function injectDrawerLink() {
        var drawer = document.querySelector('.mainDrawer .navMenuOptions, .mainDrawer-scrollContainer');
        if (!drawer || drawer.querySelector('.jh-drawer-link')) { return; }
        var link = document.createElement('a');
        link.is = 'emby-linkbutton';
        link.className = 'navMenuOption lnkMediaFolder jh-drawer-link emby-button';
        link.href = '#';
        link.innerHTML = '<span class="material-icons navMenuOptionIcon">tune</span>'
            + '<span class="navMenuOptionText">JuddHome Settings</span>';
        link.addEventListener('click', function (e) {
            e.preventDefault();
            var root = document.getElementById('juddhome-root');
            if (root) {
                openSettings(root);
            }
            document.body.click(); // close the drawer
        });
        drawer.appendChild(link);
    }

    /* ---------- takeover ---------- */

    function takeOver(container) {
        if (document.getElementById('juddhome-root')) { return; }
        container.dataset.juddhome = '1';

        ensureStyles();
        loadTemplate().then(function (html) {
            if (document.getElementById('juddhome-root')) { return; }

            // Wholesale replace the vanilla home sections with the JuddHome UI.
            Array.prototype.forEach.call(container.children, function (child) {
                child.style.display = 'none';
            });
            var mount = document.createElement('div');
            mount.innerHTML = html;
            container.appendChild(mount.firstElementChild);

            var root = document.getElementById('juddhome-root');
            root.querySelector('#jh-settings-btn').addEventListener('click', function () {
                openSettings(root);
            });

            return getJson('JuddHome/Sections').then(function (layout) {
                STATE.layout = layout;
                STATE.heroSeconds = prop(layout, 'HeroRotationSeconds') || 8;
                var sections = (prop(layout, 'Sections') || []).filter(function (s) {
                    return prop(s, 'Enabled');
                });
                var sectionsBox = root.querySelector('#jh-sections');
                var heroEnabled = sections.some(function (s) {
                    return prop(s, 'SectionType') === 'HeroBanner';
                });

                if (heroEnabled) {
                    loadHero(root);
                }

                // Fetch every row independently so one failure can't block others.
                sections.forEach(function (s) {
                    if (prop(s, 'SectionType') === 'HeroBanner') { return; }
                    loadSection(s, sectionsBox);
                });
            });
        }).catch(function (err) {
            console.error('JuddHome: failed to initialise, restoring vanilla home screen', err);
            Array.prototype.forEach.call(container.children, function (child) {
                if (child.id !== 'juddhome-root') { child.style.display = ''; }
            });
            var mount = document.getElementById('juddhome-root');
            if (mount) { mount.remove(); }
        });
    }

    function onViewShow() {
        if (!loggedIn()) { return; }
        injectDrawerLink();
        if (!isHomeRoute()) {
            stopHero();
            return;
        }
        var container = findHomeSectionsContainer();
        if (container) {
            takeOver(container);
        }
    }

    /* ---------- bootstrap ---------- */

    function start() {
        // Jellyfin fires viewshow when a page becomes visible.
        document.addEventListener('viewshow', function () {
            setTimeout(onViewShow, 50);
        });
        window.addEventListener('hashchange', function () {
            setTimeout(onViewShow, 50);
        });
        // Fallback for initial load and web clients that render asynchronously.
        var observer = new MutationObserver(function () {
            if (isHomeRoute() && !document.getElementById('juddhome-root') && findHomeSectionsContainer()) {
                onViewShow();
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });
        onViewShow();
    }

    function waitForApiClient(attempts) {
        if (apiClient()) {
            start();
            return;
        }
        if (attempts > 100) {
            console.warn('JuddHome: ApiClient never appeared; giving up');
            return;
        }
        setTimeout(function () { waitForApiClient(attempts + 1); }, 200);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { waitForApiClient(0); });
    } else {
        waitForApiClient(0);
    }
}());
