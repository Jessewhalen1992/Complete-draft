(vl-load-com)

(setq *compass-startup-loader-name* "LoadCompassOnStartup.lsp")

; Optional override.
; Uncomment and edit this if AutoCAD cannot discover the folder automatically.
; (setq *compass-startup-folder* "C:\\Compass\\CompassPlugin")

(if (not (boundp '*compass-startup-loaded*))
  (setq *compass-startup-loaded* nil)
)

(if (not (boundp '*compass-startup-hook-installed*))
  (setq *compass-startup-hook-installed* nil)
)

(if (not (boundp '*compass-startup-prev-s*))
  (setq *compass-startup-prev-s* nil)
)

(defun compass-startup--trim-dir (path)
  (if path
    (vl-string-right-trim "\\/" path)
  )
)

(defun compass-startup--script-dir (/ script-path)
  (setq script-path (findfile *compass-startup-loader-name*))
  (if script-path
    (vl-filename-directory script-path)
  )
)

(defun compass-startup--dll-path (/ base-dir)
  (setq base-dir
         (cond
           ((and (boundp '*compass-startup-folder*)
                 *compass-startup-folder*
                 (/= *compass-startup-folder* ""))
            *compass-startup-folder*)
           ((compass-startup--script-dir))
         )
  )

  (if base-dir
    (strcat (compass-startup--trim-dir base-dir) "\\Compass.dll")
  )
)

(defun compass-startup--trusted-p (folder trusted / normalized haystack)
  (setq normalized (strcat (strcase (compass-startup--trim-dir folder)) "\\"))
  (setq haystack (strcat ";" (strcase trusted) ";"))
  (not (null (vl-string-search (strcat ";" normalized ";") haystack)))
)

(defun compass-startup--ensure-trusted (dll-path / folder trusted normalized)
  (setq folder (vl-filename-directory dll-path))

  (if (and folder (> (getvar "SECURELOAD") 0))
    (progn
      (setq trusted (getvar "TRUSTEDPATHS"))
      (if (null trusted)
        (setq trusted "")
      )

      (if (not (compass-startup--trusted-p folder trusted))
        (progn
          (setq normalized (strcat (compass-startup--trim-dir folder) "\\"))
          (setvar
            "TRUSTEDPATHS"
            (if (= trusted "")
              normalized
              (strcat trusted ";" normalized)
            )
          )
        )
      )
    )
  )
)

(defun compass-startup-load (/ dll-path result)
  (cond
    (*compass-startup-loaded*
      (princ)
    )
    ((not (setq dll-path (compass-startup--dll-path)))
      (prompt
        "\nCompass: could not determine the DLL folder. Edit *compass-startup-folder* in LoadCompassOnStartup.lsp."
      )
    )
    ((not (findfile dll-path))
      (prompt (strcat "\nCompass: DLL not found at " dll-path))
    )
    (T
      (compass-startup--ensure-trusted dll-path)
      (setq result (vl-catch-all-apply 'vl-cmdf (list "_.NETLOAD" dll-path)))

      (if (vl-catch-all-error-p result)
        (prompt
          (strcat "\nCompass: NETLOAD failed: " (vl-catch-all-error-message result))
        )
        (progn
          (setq *compass-startup-loaded* T)
          (prompt (strcat "\nCompass: loaded " dll-path))
        )
      )
    )
  )

  (princ)
)

(defun c:COMPASSLOAD ()
  (setq *compass-startup-loaded* nil)
  (compass-startup-load)
)

(defun compass-startup-run-deferred ()
  (if (and (boundp '*compass-startup-prev-s*)
           *compass-startup-prev-s*)
    (apply *compass-startup-prev-s* nil)
  )
  (compass-startup-load)
  (princ)
)

(defun compass-startup-install-hook ()
  (if (not *compass-startup-hook-installed*)
    (progn
      (if (and (fboundp 'S::STARTUP)
               (not (eq (symbol-function 'S::STARTUP) (symbol-function 'compass-startup-run-deferred))))
        (setq *compass-startup-prev-s* (symbol-function 'S::STARTUP))
      )
      (defun S::STARTUP ()
        (compass-startup-run-deferred)
      )
      (setq *compass-startup-hook-installed* T)
    )
  )
  (princ)
)

(compass-startup-install-hook)
(compass-startup-load)
(princ)
