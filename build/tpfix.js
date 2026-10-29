
// offline: «Заказ түрі» фоны көрінетін ретке қарай жылжиды (sel_type_def CSS-пен бірінші тұрады)
$(function(){
   function tpFix(){
      var it = $('.tp_seg .form_im_slo').get().map(function(el, k){ return { el: el, o: parseInt(getComputedStyle(el).order) || 0, k: k } })
      it.sort(function(a, b){ return a.o - b.o || a.k - b.k })
      var a = $('.sel_type .form_im_slo_act').parent()[0]
      for (var i = 0; i < it.length; i++) if (it[i].el === a) $('.tp_seg').css('--i', i)
   }
   tpFix()
   $(document).on('click', '.sel_type .form_im_slo_i', function(){ setTimeout(tpFix, 0) })
})
